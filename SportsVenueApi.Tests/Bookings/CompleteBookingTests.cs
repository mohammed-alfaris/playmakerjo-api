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

    private async Task<Booking> SeedConfirmed(string venueId, double total, double paid)
    {
        return await _fx.Insert(new Booking
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
    }

    public CompleteBookingTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task Rejected_WhenABalanceRemains()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 5);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("20", body!.Message); // the remaining balance, so the owner knows how much to collect
        Assert.Equal("confirmed", (await _fx.LoadBooking(booking.Id))!.Status);
    }

    [Fact]
    public async Task Succeeds_WhenFullyPaid()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 25);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("completed", (await _fx.LoadBooking(booking.Id))!.Status);
    }

    [Fact]
    public async Task Succeeds_WhenPaidAmountIsWithinRoundingEpsilon()
    {
        // JOD is quoted to 3dp; a 20% deposit on odd totals leaves a sub-fil remainder that
        // is float noise, not a real debt (see PaymentLedger.Epsilon). A booking sitting at
        // 24.9998/25 must not be permanently blocked from ever being marked complete.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedConfirmed(venue.Id, total: 25, paid: 24.9998);

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
