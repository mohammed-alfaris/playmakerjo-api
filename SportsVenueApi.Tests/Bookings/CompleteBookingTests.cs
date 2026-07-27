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
/// independent logic and is not covered by this rule.
///
/// "Completed" must mean the visit is settled, not just that it happened. Completed always
/// counts as attended, and CustomersController's owing figure only looks at attended
/// bookings — so letting an underpaid booking through here is how a genuine debt stops being
/// visible as one at all.
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
    private async Task<Booking> SeedConfirmed(string venueId, double total, double paid)
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

    // -------------------------------------------------------- settle-balance

    [Fact]
    public async Task SettleBalance_ClosesTheGapToZero_ThenCompleteSucceeds()
    {
        // The whole point: Complete's new rule is only fair if there is a way to make a
        // booking fully paid. This is that way.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 5);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var settle = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/settle-balance", null);
        Assert.Equal(HttpStatusCode.OK, settle.StatusCode);
        Assert.Equal(25, (await _fx.LoadBooking(booking.Id))!.AmountPaid, 3);

        var complete = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    [Fact]
    public async Task SettleBalance_WritesAMatchingLedgerRow()
    {
        // The ledger's invariant (PaymentLedgerTests): sum(payments.amount) == amount_paid.
        // A settle that updated the field without writing the row would violate it silently.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 5);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        await client.PatchAsync($"/api/v1/bookings/{booking.Id}/settle-balance", null);

        var payments = await _fx.LoadPayments(booking.Id);
        var row = await _fx.LoadBooking(booking.Id);
        Assert.Equal(2, payments.Count);            // 5 deposit + 20 balance
        Assert.Equal(20, payments.Last().Amount, 3);
        Assert.Equal("cash", payments.Last().Method);
        Assert.Equal(row!.AmountPaid, payments.Sum(p => p.Amount), 3);
    }

    [Fact]
    public async Task SettleBalance_Rejected_WhenAlreadyFullyPaid()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 25);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/settle-balance", null);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SettleBalance_Rejected_OnACancelledBooking()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await _fx.Insert(new Booking
        {
            VenueId = venue.Id,
            PlayerId = _fx.PlayerId,
            Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(-1),
            StartTime = "10:00",
            Duration = 60,
            Amount = 25,
            TotalAmount = 25,
            AmountPaid = 5,
            Status = "cancelled",
        });

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/settle-balance", null);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task SettleBalance_ACompetitorsBooking_CannotBeTouched()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var theirs = await SeedConfirmed(venueB.Id, total: 25, paid: 5);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{theirs.Id}/settle-balance", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
