using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Jobs;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Jobs;

/// <summary>
/// The expiry job releases slots held by app bookings nobody paid for.
///
/// It is the first thing in this system that destroys a row without a human asking it to,
/// so the tests are weighted heavily toward what it must NOT touch. The safety model is
/// arming: only the app-booking path stamps a payment_deadline_at, and a NULL deadline means
/// immune. Counter bookings, recurring series and every row that predates the column are
/// therefore safe by construction rather than by a WHERE clause somebody has to maintain —
/// and <see cref="CounterBookingHeldForARegular_IsNeverTouched"/> is the test that proves it.
///
/// Time is simulated by passing a future nowUtc to SweepAsync rather than by backdating rows
/// or sleeping. There is no clock abstraction anywhere in this codebase (49 direct
/// DateTime.UtcNow sites) and adding one purely for this would dwarf the feature.
/// </summary>
[Collection("Api")]
public class BookingExpiryTests
{
    private const string ProofImage = "data:image/png;base64,iVBORw0KGgo=";

    private readonly DatabaseFixture _fx;

    public BookingExpiryTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(17).ToString("yyyy-MM-dd");

    /// <summary>Well past any deadline a 2-hour window could produce.</summary>
    private static DateTime LongAfter => DateTime.UtcNow.AddDays(1);

    private async Task<SweepResult> Sweep(DateTime nowUtc, int batchSize = 200)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var sweep = scope.ServiceProvider.GetRequiredService<UnpaidBookingSweep>();
        return await sweep.SweepAsync(nowUtc, batchSize);
    }

    private static object AppBooking(string venueId, string startTime) => new
    {
        venueId,
        sport = "basketball",
        date = FutureDate,
        startTime,
        duration = 60,
        paymentMethod = "cliq",
    };

    private static object CounterBooking(string venueId, string startTime, bool paid) => new
    {
        venueId,
        sport = "basketball",
        date = FutureDate,
        startTime,
        duration = 60,
        paymentMethod = "cliq",
        isManual = true,
        customerPaid = paid,
        customerPhone = "0791234701",
        customerName = "خالد النتور",
    };

    private static async Task<string> Book(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/api/v1/bookings", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var parsed = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        return parsed!.Data!.Id;
    }

    private async Task<(Venue Venue, string BookingId)> UnpaidAppBooking(string startTime = "10:00")
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var id = await Book(_fx.CreateClientFor(player.Id, "player"), AppBooking(venue.Id, startTime));
        return (venue, id);
    }

    // -----------------------------------------------------------------------------------
    // Arming — who gets a deadline at all
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnpaidAppBooking_IsArmed()
    {
        var (_, bookingId) = await UnpaidAppBooking();

        var booking = await _fx.LoadBooking(bookingId);
        Assert.Equal("pending_payment", booking!.Status);
        Assert.NotNull(booking.PaymentDeadlineAt);
        Assert.True(booking.PaymentDeadlineAt > DateTime.UtcNow, "a new booking must not be born expired");
        Assert.Null(booking.AutoCancelledAt);
    }

    [Fact]
    public async Task ACounterBooking_IsNeverArmed()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var paid = await Book(client, CounterBooking(venue.Id, "11:00", paid: true));
        var unpaid = await Book(client, CounterBooking(venue.Id, "12:00", paid: false));

        // Especially the unpaid one. "Unpaid and old" describes both an abandoned checkout
        // and a slot the owner is deliberately holding for someone he knows.
        Assert.Null((await _fx.LoadBooking(paid))!.PaymentDeadlineAt);
        Assert.Null((await _fx.LoadBooking(unpaid))!.PaymentDeadlineAt);
    }

    [Fact]
    public async Task ARecurringSeries_IsNeverArmed()
    {
        // Every occurrence is inserted at the same instant but dated up to three months out,
        // all at pending_payment. Age-based expiry would cancel the whole series at once.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings/recurring", new
        {
            venueId = venue.Id,
            sport = "basketball",
            startDate = FutureDate,
            endDate = PlatformConstants.JordanToday().AddDays(38).ToString("yyyy-MM-dd"),
            startTime = "13:00",
            duration = 60,
            recurrenceType = "weekly",
            paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var rows = await _fx.LoadVenueBookings(venue.Id);
        Assert.NotEmpty(rows);
        Assert.All(rows, b => Assert.Null(b.PaymentDeadlineAt));
    }

    // -----------------------------------------------------------------------------------
    // Disarming — leaving pending_payment must clear the deadline
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task UploadingProof_Disarms()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");
        var bookingId = await Book(client, AppBooking(venue.Id, "14:00"));

        await client.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });

        // The customer has paid; the booking now waits on the OWNER to look at a screenshot.
        // Expiring it here would punish the customer for the venue's inaction.
        Assert.Null((await _fx.LoadBooking(bookingId))!.PaymentDeadlineAt);

        Assert.Equal(0, (await Sweep(LongAfter)).Cancelled);
        Assert.Equal("pending_review", (await _fx.LoadBooking(bookingId))!.Status);
    }

    [Fact]
    public async Task RejectingProof_ReArmsWithAFreshWindow()
    {
        // The reason the deadline is a stored instant rather than an age derived from
        // CreatedAt: a bounced proof puts the booking back to pending_payment with CreatedAt
        // untouched, and bookings has no updated_at. An age rule would cancel it instantly.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");
        var bookingId = await Book(client, AppBooking(venue.Id, "15:00"));

        var armedAt = (await _fx.LoadBooking(bookingId))!.PaymentDeadlineAt;

        await client.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });
        await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/review-proof", new { approved = false, note = "Blurry" });

        var after = await _fx.LoadBooking(bookingId);
        Assert.Equal("pending_payment", after!.Status);
        Assert.NotNull(after.PaymentDeadlineAt);
        Assert.True(after.PaymentDeadlineAt >= armedAt, "the re-upload window must restart, not resume");
        Assert.True(after.PaymentDeadlineAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task ApprovingProof_Disarms()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");
        var bookingId = await Book(client, AppBooking(venue.Id, "16:00"));

        await client.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });
        await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/review-proof", new { approved = true });

        Assert.Null((await _fx.LoadBooking(bookingId))!.PaymentDeadlineAt);
    }

    [Fact]
    public async Task ConfirmingAndCancelling_BothDisarm()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var confirmed = await Book(_fx.CreateClientFor(player.Id, "player"), AppBooking(venue.Id, "17:00"));
        await owner.PatchAsync($"/api/v1/bookings/{confirmed}/confirm", null);
        Assert.Null((await _fx.LoadBooking(confirmed))!.PaymentDeadlineAt);

        var cancelled = await Book(_fx.CreateClientFor(player.Id, "player"), AppBooking(venue.Id, "18:00"));
        await owner.PatchAsync($"/api/v1/bookings/{cancelled}/cancel", null);

        var row = await _fx.LoadBooking(cancelled);
        Assert.Null(row!.PaymentDeadlineAt);
        // A human cancelled this one. If the deadline survived, a later sweep would stamp
        // auto_cancelled_at on it and rewrite whose decision it was.
        Assert.Null(row.AutoCancelledAt);
    }

    // -----------------------------------------------------------------------------------
    // The sweep
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task AnExpiredHold_IsReleasedAndMarkedAsAutomatic()
    {
        var (_, bookingId) = await UnpaidAppBooking("19:00");

        var result = await Sweep(LongAfter);
        Assert.True(result.Cancelled >= 1);

        var booking = await _fx.LoadBooking(bookingId);
        // "cancelled" and not a new "expired" status, because all five occupancy predicates
        // are written as `Status != "cancelled"` — an "expired" row would keep the pitch
        // blocked forever while the job reported success.
        Assert.Equal("cancelled", booking!.Status);
        Assert.NotNull(booking.AutoCancelledAt);
        Assert.Null(booking.PaymentDeadlineAt);
    }

    [Fact]
    public async Task AHoldWhoseDeadlineHasNotPassed_IsUntouched()
    {
        var (_, bookingId) = await UnpaidAppBooking("20:00");

        await Sweep(DateTime.UtcNow);

        var booking = await _fx.LoadBooking(bookingId);
        Assert.Equal("pending_payment", booking!.Status);
        Assert.Null(booking.AutoCancelledAt);
    }

    [Fact]
    public async Task CounterBookingHeldForARegular_IsNeverTouched()
    {
        // A phone booking taken on Sunday for Thursday, to be paid on arrival, is
        // indistinguishable from an abandoned checkout by every money-shaped predicate:
        // amount_paid = 0 and deposit_paid = 0 on both. Cancelling it overrules a decision
        // the owner made deliberately, in his own venue, for a customer he knows.
        //
        // Two independent mechanisms spare it — it is never armed, AND it is created
        // "confirmed" rather than "pending_payment". Verified by removal: unguarding the
        // arming leaves this test green, because the status clause still catches it. That
        // redundancy is the point, so the isolated arming guard is pinned separately by
        // ACounterBooking_IsNeverArmed and the is_manual clause by
        // AManualRowSomehowAtPendingPayment_IsStillSpared.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var bookingId = await Book(
            _fx.CreateClientFor(_fx.OwnerAId, "venue_owner"),
            CounterBooking(venue.Id, "21:00", paid: false));

        await Sweep(LongAfter);

        var booking = await _fx.LoadBooking(bookingId);
        Assert.Equal("confirmed", booking!.Status);
        Assert.Null(booking.AutoCancelledAt);
        Assert.Equal(0, booking.AmountPaid);
    }

    [Fact]
    public async Task AManualRowSomehowAtPendingPayment_IsStillSpared()
    {
        // Isolates the `!b.IsManual` clause. No current path produces this combination —
        // counter bookings are created "confirmed" — so it exists purely to pin the last
        // line of defence should some future path ever create a manual row awaiting payment.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = _fx.OwnerAId, Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(21), StartTime = "09:00", Duration = 60,
            Amount = 20, TotalAmount = 20, DepositAmount = 4,
            Status = "pending_payment", PaymentMethod = "cliq",
            IsManual = true,
            PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(-5),
        });

        await Sweep(LongAfter);

        Assert.Equal("pending_payment", (await _fx.LoadBooking(booking.Id))!.Status);
    }

    [Fact]
    public async Task RowsPredatingTheColumn_AreImmune()
    {
        // Every booking that existed before the migration carries a NULL deadline. This is
        // what makes "do not backfill payment_deadline_at" more than a comment: the column
        // being empty is the only thing protecting the entire history.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var legacy = await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = player.Id, Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(19), StartTime = "09:00", Duration = 60,
            Amount = 20, TotalAmount = 20, DepositAmount = 4,
            Status = "pending_payment", PaymentMethod = "cliq",
            CreatedAt = DateTime.UtcNow.AddDays(-120),
            PaymentDeadlineAt = null,
        });

        await Sweep(LongAfter);

        Assert.Equal("pending_payment", (await _fx.LoadBooking(legacy.Id))!.Status);
    }

    [Fact]
    public async Task AnAlreadyPaidBookingWithAStaleDeadline_IsSpared()
    {
        // Defence in depth. Settling disarms the row, so this state should be unreachable —
        // but if some future path forgets, refusing to cancel a booking whose money has
        // arrived is the failure we want.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var booking = await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = player.Id, Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(20), StartTime = "09:00", Duration = 60,
            Amount = 20, TotalAmount = 20, DepositAmount = 4,
            Status = "pending_payment", PaymentMethod = "cliq",
            AmountPaid = 4, DepositPaid = true,
            PaymentDeadlineAt = DateTime.UtcNow.AddMinutes(-5),
        });

        await Sweep(LongAfter);

        Assert.Equal("pending_payment", (await _fx.LoadBooking(booking.Id))!.Status);
    }

    [Fact]
    public async Task SweepingTwice_CancelsNothingTheSecondTime()
    {
        await UnpaidAppBooking("08:00");

        var first = await Sweep(LongAfter);
        var second = await Sweep(LongAfter);

        Assert.True(first.Cancelled >= 1);
        Assert.Equal(0, second.Cancelled);
        Assert.Equal(0, second.Considered);
    }

    // -----------------------------------------------------------------------------------
    // The point of the whole exercise
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task TheFreedSlotCanActuallyBeBookedAgain()
    {
        // Everything above could pass while the feature is a no-op. This is the only test
        // that proves the slot came back — and it is exactly the test that would have caught
        // an "expired" status, which frees nothing because every conflict scan asks
        // `Status != "cancelled"`.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var first = await _fx.CreatePlayer();
        var second = await _fx.CreatePlayer();

        await Book(_fx.CreateClientFor(first.Id, "player"), AppBooking(venue.Id, "09:00"));

        var blocked = await _fx.CreateClientFor(second.Id, "player")
            .PostAsJsonAsync("/api/v1/bookings", AppBooking(venue.Id, "09:00"));
        Assert.NotEqual(HttpStatusCode.OK, blocked.StatusCode);

        await Sweep(LongAfter);

        var afterSweep = await _fx.CreateClientFor(second.Id, "player")
            .PostAsJsonAsync("/api/v1/bookings", AppBooking(venue.Id, "09:00"));
        Assert.Equal(HttpStatusCode.OK, afterSweep.StatusCode);
    }

    [Fact]
    public async Task APaymentArrivingDuringTheSweepWindow_Wins()
    {
        // The UPDATE repeats its full predicate in the WHERE clause, so a customer who
        // uploads proof between the sweep's read and its write simply no longer matches.
        // Freeing a slot can never cause a double-booking; cancelling a paid one can.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");
        var bookingId = await Book(client, AppBooking(venue.Id, "10:00"));

        var upload = client.PatchAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });
        var sweep = Sweep(LongAfter);
        await Task.WhenAll(upload, sweep);

        var booking = await _fx.LoadBooking(bookingId);
        // Whichever ordering the race produced, the row must never end up cancelled with
        // proof attached — that is money received against a slot given away.
        if (booking!.PaymentProof != null)
            Assert.NotEqual("cancelled", booking.Status);
    }

    [Fact]
    public async Task BatchSizeIsRespected()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        foreach (var time in new[] { "11:00", "12:00", "13:00" })
            await Book(_fx.CreateClientFor((await _fx.CreatePlayer()).Id, "player"), AppBooking(venue.Id, time));

        var result = await Sweep(LongAfter, batchSize: 2);

        // A job with no bound competes with request handling on a 1 vCPU box.
        Assert.Equal(2, result.Considered);
        Assert.Equal(2, result.Cancelled);
    }
}
