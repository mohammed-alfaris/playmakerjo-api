using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Payments;

/// <summary>
/// Until now nothing in the application had ever written a payments row: every movement of
/// money was a boolean and a mutable number on the booking itself. That answers "how much is
/// paid" and nothing else — not who said so, not when, not by what means, and not whether the
/// figure was quietly changed afterwards.
///
/// These tests pin the two properties that make the new ledger worth trusting:
///
///   1. COMPLETENESS — every path that raises amount_paid leaves a row behind, and the rows
///      for a booking sum exactly to it. Asserted directly, per test, via <see cref="AssertInvariant"/>.
///   2. IMMUTABILITY — a written row cannot be edited or deleted, so the history of a
///      correction survives the correction.
///
/// A ledger with either property missing is just another table.
/// </summary>
[Collection("Api")]
public class PaymentLedgerTests
{
    private const string ProofImage = "data:image/png;base64,iVBORw0KGgo=";

    private readonly DatabaseFixture _fx;

    public PaymentLedgerTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(13).ToString("yyyy-MM-dd");

    // Distinct start times per test: the venues are throwaway, but a shared one would make
    // these fail on slot conflicts rather than on anything they are meant to measure.
    private static object Manual(string venueId, string startTime, bool paid, string phone = "0791234501") => new
    {
        venueId,
        sport = "basketball",
        date = FutureDate,
        startTime,
        duration = 60,
        paymentMethod = "cliq",
        isManual = true,
        customerPaid = paid,
        customerPhone = phone,
        customerName = "خالد الشوبكي",
    };

    private static async Task<string> Book(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/api/v1/bookings", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var parsed = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        return parsed!.Data!.Id;
    }

    /// <summary>
    /// The property the whole design rests on: the ledger rows for a booking sum to the
    /// booking's own amount_paid. If a write path forgets to record, or something mutates
    /// amount_paid behind the ledger's back, this is what notices.
    /// </summary>
    private async Task AssertInvariant(string bookingId)
    {
        var booking = await _fx.LoadBooking(bookingId);
        var rows = await _fx.LoadPayments(bookingId);
        Assert.Equal(booking!.AmountPaid, rows.Sum(p => p.Amount), 3);
    }

    // -----------------------------------------------------------------------------------
    // Completeness — every path that takes money leaves evidence
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task CounterBookingPaidOnTheSpot_RecordsOneFullReceipt()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var bookingId = await Book(client, Manual(venue.Id, "09:00", paid: true));

        var rows = await _fx.LoadPayments(bookingId);
        var row = Assert.Single(rows);
        Assert.Equal(20, row.Amount);            // 1h at 20/hour
        Assert.Equal("full", row.Kind);
        Assert.Equal("paid", row.Status);
        Assert.Equal(venue.Id, row.VenueId);

        // The audit half: the owner who took the cash is named on the row.
        Assert.Equal(_fx.OwnerAId, row.RecordedByUserId);

        // And the payer is the CUSTOMER, not the owner's own account — which is what the
        // booking's player_id holds on a counter booking.
        Assert.NotNull(row.CustomerId);
        Assert.Equal(_fx.OwnerAId, row.PlayerId);

        await AssertInvariant(bookingId);
    }

    [Fact]
    public async Task CounterBookingNotYetPaid_RecordsNothing()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var bookingId = await Book(client, Manual(venue.Id, "10:00", paid: false));

        // Money that has not arrived is represented by the ABSENCE of a row. Writing a
        // zero-amount or "pending" row would put an event in the owner's history that
        // never happened, and would make the day's takings unreadable at a glance.
        Assert.Empty(await _fx.LoadPayments(bookingId));
        await AssertInvariant(bookingId);
    }

    [Fact]
    public async Task SettlingLater_RecordsTheBalance_AndASecondTapAddsNothing()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var bookingId = await Book(client, Manual(venue.Id, "11:00", paid: false));

        var first = await client.PatchAsync($"/api/v1/bookings/{bookingId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var row = Assert.Single(await _fx.LoadPayments(bookingId));
        Assert.Equal(20, row.Amount);
        Assert.Equal("full", row.Kind);   // nothing had been taken before, so this is the lot

        // A double-tap on a slow connection must not book the takings twice. It succeeds —
        // the end state asked for is already true — but writes no second row.
        var second = await client.PatchAsync($"/api/v1/bookings/{bookingId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        Assert.Single(await _fx.LoadPayments(bookingId));
        await AssertInvariant(bookingId);
    }

    [Fact]
    public async Task DepositThenBalance_RecordsTwoRowsThatSumToTheTotal()
    {
        // The case a single amount_paid column cannot express: 20% up front by CliQ, the
        // rest in cash on the day. One number can only ever show the end state.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var playerClient = _fx.CreateClientFor(player.Id, "player");

        var res = await playerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = FutureDate,
            startTime = "12:00", duration = 60, paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bookingId = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        await playerClient.PatchAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });

        var ownerClient = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var approve = await ownerClient.PatchAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/review-proof", new { approved = true });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var settle = await ownerClient.PatchAsync($"/api/v1/bookings/{bookingId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, settle.StatusCode);

        var rows = await _fx.LoadPayments(bookingId);
        Assert.Equal(2, rows.Count);
        Assert.Equal("deposit", rows[0].Kind);
        Assert.Equal(4, rows[0].Amount);          // 20% of 20
        Assert.Equal("balance", rows[1].Kind);
        Assert.Equal(16, rows[1].Amount);         // and the remainder, not the total again
        Assert.Equal(20, rows.Sum(p => p.Amount));

        await AssertInvariant(bookingId);
    }

    [Fact]
    public async Task ApprovingAProof_NamesTheReviewer()
    {
        // Approving a screenshot is a judgement call about whether a transfer is real.
        // Somebody has to be answerable for it, and staff act on the owner's behalf.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var playerClient = _fx.CreateClientFor(player.Id, "player");

        var res = await playerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = FutureDate,
            startTime = "13:00", duration = 60, paymentMethod = "cliq",
        });
        var bookingId = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        await playerClient.PatchAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });

        var staff = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var approve = await staff.PatchAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/review-proof", new { approved = true });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        var row = Assert.Single(await _fx.LoadPayments(bookingId));
        Assert.Equal(_fx.StaffAWriteId, row.RecordedByUserId);
        Assert.Equal("deposit", row.Kind);
        Assert.Equal("cliq", row.Method);
        await AssertInvariant(bookingId);
    }

    [Fact]
    public async Task RejectingAProof_RecordsNothing()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var playerClient = _fx.CreateClientFor(player.Id, "player");

        var res = await playerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = FutureDate,
            startTime = "14:00", duration = 60, paymentMethod = "cliq",
        });
        var bookingId = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        await playerClient.PatchAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });

        var ownerClient = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        await ownerClient.PatchAsJsonAsync(
            $"/api/v1/bookings/{bookingId}/review-proof", new { approved = false, note = "Wrong amount" });

        Assert.Empty(await _fx.LoadPayments(bookingId));
        await AssertInvariant(bookingId);
    }

    [Fact]
    public async Task ConfirmingAWaitingBooking_NoLongerClaimsPaidWithoutAnAmount()
    {
        // The old Confirm set deposit_paid = true and left amount_paid at zero, so the row
        // asserted it had been paid and had received nothing simultaneously — and every
        // customer's balance owed was overstated by exactly the deposit.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var playerClient = _fx.CreateClientFor(player.Id, "player");

        var res = await playerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = FutureDate,
            startTime = "15:00", duration = 60, paymentMethod = "cliq",
        });
        var bookingId = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var ownerClient = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var confirm = await ownerClient.PatchAsync($"/api/v1/bookings/{bookingId}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);

        var booking = await _fx.LoadBooking(bookingId);
        Assert.True(booking!.DepositPaid);
        Assert.Equal(4, booking.AmountPaid);      // and now it says how much

        var row = Assert.Single(await _fx.LoadPayments(bookingId));
        Assert.Equal("deposit", row.Kind);
        Assert.Equal(_fx.OwnerAId, row.RecordedByUserId);
        await AssertInvariant(bookingId);
    }

    // -----------------------------------------------------------------------------------
    // Immutability — history survives corrections
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task ARecordedPaymentCannotBeEdited()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var bookingId = await Book(client, Manual(venue.Id, "16:00", paid: true));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Payments.FirstAsync(p => p.BookingId == bookingId);
        row.Amount = 999;

        // Enforced in the DbContext rather than by convention, so it also catches the
        // well-meaning endpoint somebody adds a year from now to "fix a typo".
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);

        var stored = Assert.Single(await _fx.LoadPayments(bookingId));
        Assert.Equal(20, stored.Amount);
    }

    [Fact]
    public async Task ARecordedPaymentCannotBeDeleted()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var bookingId = await Book(client, Manual(venue.Id, "17:00", paid: true));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Payments.Remove(await db.Payments.FirstAsync(p => p.BookingId == bookingId));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);

        Assert.Single(await _fx.LoadPayments(bookingId));
    }

    // -----------------------------------------------------------------------------------
    // Scoping — the ledger is the most sensitive read in the product
    // -----------------------------------------------------------------------------------

    private async Task<List<PaymentRow>> ReadLedger(HttpClient client, string query = "")
    {
        var res = await client.GetAsync($"/api/v1/payments?limit=100{query}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var parsed = await res.Content.ReadFromJsonAsync<ApiResponse<List<PaymentRow>>>();
        return parsed!.Data ?? [];
    }

    private sealed record PaymentRow(
        string Id, string BookingRef, string PayerName, double Amount, string Kind);

    [Fact]
    public async Task OwnerSeesOnlyTheirOwnTakings()
    {
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);

        var aBooking = await Book(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"),
            Manual(venueA.Id, "18:00", paid: true, phone: "0791234511"));
        var bBooking = await Book(_fx.CreateClientFor(_fx.OwnerBId, "venue_owner"),
            Manual(venueB.Id, "18:00", paid: true, phone: "0791234512"));

        var seenByA = await ReadLedger(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"));
        Assert.Contains(seenByA, r => r.BookingRef == aBooking);
        Assert.DoesNotContain(seenByA, r => r.BookingRef == bBooking);

        var seenByB = await ReadLedger(_fx.CreateClientFor(_fx.OwnerBId, "venue_owner"));
        Assert.Contains(seenByB, r => r.BookingRef == bBooking);
        Assert.DoesNotContain(seenByB, r => r.BookingRef == aBooking);
    }

    [Fact]
    public async Task OwnerCannotReadAnotherOwnersLedgerByAskingForIt()
    {
        // owner_id is a query parameter. It must be ignored for anyone but an admin —
        // this is the exact shape of leak the deny-list scoping used to allow everywhere.
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var bBooking = await Book(_fx.CreateClientFor(_fx.OwnerBId, "venue_owner"),
            Manual(venueB.Id, "19:00", paid: true, phone: "0791234513"));

        var rows = await ReadLedger(
            _fx.CreateClientFor(_fx.OwnerAId, "venue_owner"), $"&owner_id={_fx.OwnerBId}");

        Assert.DoesNotContain(rows, r => r.BookingRef == bBooking);
    }

    [Fact]
    public async Task UnlinkedStaffSeeAnEmptyLedgerRatherThanEveryones()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        await Book(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"),
            Manual(venue.Id, "20:00", paid: true, phone: "0791234514"));

        var client = await _fx.CreateClientForUserAsync(_fx.StaffUnlinkedId);
        Assert.Empty(await ReadLedger(client));
    }

    [Fact]
    public async Task PlayersSeeOnlyTheirOwnReceipts()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var ownerBooking = await Book(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"),
            Manual(venue.Id, "21:00", paid: true, phone: "0791234515"));

        var player = await _fx.CreatePlayer();
        var rows = await ReadLedger(_fx.CreateClientFor(player.Id, "player"));

        Assert.DoesNotContain(rows, r => r.BookingRef == ownerBooking);
    }

    [Fact]
    public async Task TheLedgerNamesTheCustomerNotTheOwnersOwnAccount()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var bookingId = await Book(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"),
            Manual(venue.Id, "08:00", paid: true, phone: "0791234516"));

        var rows = await ReadLedger(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"));
        var row = Assert.Single(rows, r => r.BookingRef == bookingId);

        // player_id on a counter booking is the owner. Printing that would repeat the
        // headline defect — a schedule showing the owner's own name eight times.
        Assert.Equal("خالد الشوبكي", row.PayerName);
    }

    [Fact]
    public async Task TotalsCoverTheWholeFilterNotJustThePage()
    {
        // Scoped to one throwaway venue rather than to a whole owner: other tests in this
        // class share Owner A and B, and a total over everything they happened to book
        // would make this assertion depend on execution order.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        foreach (var (time, i) in new[] { "09:00", "10:00", "11:00" }.Select((t, i) => (t, i)))
            await Book(client, Manual(venue.Id, time, paid: true, phone: $"079123452{i}"));

        var res = await client.GetAsync($"/api/v1/payments/totals?venue_id={venue.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var parsed = await res.Content.ReadFromJsonAsync<ApiResponse<TotalsRow>>();

        // Three bookings at 20 each. A total that silently meant "the current page" is the
        // kind of number an owner reconciles against his cash box once and never trusts again.
        Assert.Equal(3, parsed!.Data!.Count);
        Assert.Equal(60, parsed.Data.Total);
        Assert.Equal(60, parsed.Data.ByMethod["cliq"]);
    }

    private sealed record TotalsRow(int Count, double Total, Dictionary<string, double> ByMethod);
}
