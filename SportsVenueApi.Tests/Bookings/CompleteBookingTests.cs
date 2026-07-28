using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

/// <summary>
/// PATCH /api/v1/bookings/{id}/complete — the single explicit "mark completed" action, as
/// opposed to the bulk attendance-confirm sweep (AttendanceReviewTests), which has its own
/// independent logic and is not covered by these rules.
///
/// Completing is ONE act at the counter: he played and he paid. So this endpoint collects any
/// outstanding balance itself rather than refusing until someone settles it separately.
///
/// That coupling is load-bearing. Completed always counts as attended, and the owing figure in
/// CustomersController only looks at attended bookings — so a completion that left a balance
/// behind would make a real debt silently invisible. The customer who played and did NOT pay is
/// represented by the absence of this call: the booking stays confirmed, the date passes, and the
/// derived attended rule reports it as owed.
///
/// For money WITHOUT completion (an upcoming slot prepaid, or a pending_payment booking settled
/// at the counter) the endpoint is mark-paid — see PaymentLedgerTests.
/// </summary>
[Collection("Api")]
public class CompleteBookingTests
{
    private readonly DatabaseFixture _fx;

    /// <summary>
    /// A confirmed past booking that has already taken <paramref name="paid"/> up front — WITH the
    /// matching ledger row. Seeding amount_paid without its payment row would start the test from a
    /// state the application can never actually produce (it would already violate
    /// SUM(payments)==amount_paid), and any invariant assertion made from there would be measuring
    /// the fixture rather than the code.
    /// </summary>
    private async Task<Booking> SeedConfirmed(
        string venueId, double total, double paid, DateTime? deadline = null)
    {
        var booking = await _fx.Insert(new Booking
        {
            VenueId = venueId,
            PlayerId = _fx.PlayerId,
            Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(-1),
            StartTime = "10:00",
            Duration = 60,
            Amount = total,
            TotalAmount = total,
            AmountPaid = paid,
            Status = "confirmed",
            // A counter booking, which is the case that actually carries a balance to the day.
            // It also decides what PaymentLedger records as the method: with no PaymentMethod
            // on the row, IsManual resolves it to "cash", which is what changing hands at the
            // counter really is.
            IsManual = true,
            PaymentDeadlineAt = deadline,
        });

        if (paid > 0)
        {
            await _fx.Insert(new Payment
            {
                BookingId = booking.Id,
                PlayerId = _fx.PlayerId,
                VenueId = venueId,
                Amount = paid,
                Method = "cliq",
                Kind = "deposit",
                Status = "paid",
                Date = booking.Date.AddHours(-6),
            });
        }

        return booking;
    }

    public CompleteBookingTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task CollectsTheOutstandingBalance_AndCompletes()
    {
        // One act at the counter, not two: he played and he paid. The owner taps once.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 5);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = await _fx.LoadBooking(booking.Id);
        Assert.Equal("completed", row!.Status);
        Assert.Equal(25, row.AmountPaid, 3);
    }

    [Fact]
    public async Task CollectingOnCompletion_WritesTheMatchingLedgerRow()
    {
        // The append-only ledger's invariant is SUM(payments) == amount_paid. Moving the field
        // without writing the row would break it silently — and this path moves the field.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 5);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);

        var payments = await _fx.LoadPayments(booking.Id);
        var row = await _fx.LoadBooking(booking.Id);
        // The 5 deposit that was already there, plus the 20 collected at the counter.
        Assert.Equal(2, payments.Count);
        Assert.Equal(20, payments.Last().Amount, 3);
        Assert.Equal("cash", payments.Last().Method);
        // The invariant itself, end to end.
        Assert.Equal(row!.AmountPaid, payments.Sum(p => p.Amount), 3);
    }

    [Fact]
    public async Task Succeeds_WhenAlreadyFullyPaid_WithoutWritingAZeroRow()
    {
        // Nothing moved, so nothing is recorded: a 0 JOD ledger entry would put a payment
        // event in the owner's history that never happened.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 25);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("completed", (await _fx.LoadBooking(booking.Id))!.Status);
        // Only the original payment — completion added nothing.
        Assert.Single(await _fx.LoadPayments(booking.Id));
    }

    [Fact]
    public async Task Succeeds_WhenPaidAmountIsWithinRoundingEpsilon()
    {
        // JOD is quoted to 3dp; a 20% deposit on odd totals leaves a sub-fil remainder that
        // is float noise, not a real debt (see PaymentLedger.Epsilon). That must not produce
        // a junk 0.0002 JOD "collection" row on completion.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 24.9998);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        // No junk 0.0002 JOD "collection" row for float noise.
        Assert.Single(await _fx.LoadPayments(booking.Id));
    }

    [Fact]
    public async Task ACompetitorsBooking_CannotBeCompleted()
    {
        // Completing now moves money, so the permission boundary matters more than before.
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var theirs = await SeedConfirmed(venueB.Id, total: 25, paid: 5);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{theirs.Id}/complete", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(5, (await _fx.LoadBooking(theirs.Id))!.AmountPaid, 3);
    }

    [Fact]
    public async Task Completing_DisarmsThePaymentDeadline()
    {
        // Cancel disarms for a stated reason: a live deadline on a terminal row lets the sweep
        // stamp AutoCancelledAt on it later and rewrite whose decision it was. "Completed" is
        // exactly as terminal as "cancelled". UnpaidBookingSweep only reads pending_payment
        // today, so this is defence in depth — but the two terminal paths must not disagree.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(
            venue.Id, total: 25, paid: 5, deadline: DateTime.UtcNow.AddHours(2));

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);

        Assert.Null((await _fx.LoadBooking(booking.Id))!.PaymentDeadlineAt);
    }
}
