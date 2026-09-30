using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

/// <summary>
/// The bulk "everyone came" prompt and money.
///
/// Two rules pull against each other here and both matter:
///
///  1. Confirming attendance must NEVER record a payment. One tap answering "did these
///     people turn up?" cannot also assert that every one of them paid — that would
///     fabricate money in a ledger that is append-only and cannot be corrected.
///
///  2. A booking completed that way must still be settleable afterwards. This was the trap:
///     Complete refuses a non-confirmed booking, so once the prompt had completed a session
///     the balance was unreachable through every screen and the customer read as owing
///     forever. The money was real and the product had no way to take it.
/// </summary>
[Collection("Api")]
public class BulkAttendanceMoneyTests
{
    private readonly DatabaseFixture _fx;

    public BulkAttendanceMoneyTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>An attended-but-unpaid counter booking, exactly as the prompt finds them.</summary>
    private async Task<Booking> PastUnpaidBooking(string venueId, string time)
    {
        var player = await _fx.CreatePlayer();
        return await _fx.Insert(new Booking
        {
            VenueId = venueId, PlayerId = player.Id, Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(-2), StartTime = time, Duration = 60,
            Amount = 20, TotalAmount = 20, DepositAmount = 4,
            Status = "confirmed", PaymentMethod = "cliq",
            IsManual = true, AmountPaid = 0,
        });
    }

    [Fact]
    public async Task ConfirmingAttendanceRecordsNoPayment()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await PastUnpaidBooking(venue.Id, "18:00");
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await owner.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { booking.Id } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var after = await _fx.LoadBooking(booking.Id);
        Assert.Equal("completed", after!.Status);
        Assert.Equal(0, after.AmountPaid);

        // Nothing invented. The ledger is append-only; a payment written here could never
        // be taken back.
        Assert.Empty(await _fx.LoadPayments(booking.Id));
    }

    [Fact]
    public async Task TheBalanceIsStillCollectableAfterwards()
    {
        // The dead end. Before this, Complete refused the now-completed booking and nothing
        // else offered to take the money, so the customer owed 20 JOD forever.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await PastUnpaidBooking(venue.Id, "19:00");
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        await owner.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { booking.Id } });

        var settle = await owner.PatchAsync($"/api/v1/bookings/{booking.Id}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, settle.StatusCode);

        var after = await _fx.LoadBooking(booking.Id);
        Assert.Equal(20, after!.AmountPaid);
        Assert.Equal("completed", after.Status);

        // And now the money is real, with a row behind it.
        var row = Assert.Single(await _fx.LoadPayments(booking.Id));
        Assert.Equal(20, row.Amount);
        Assert.Equal(_fx.OwnerAId, row.RecordedByUserId);
    }

    [Fact]
    public async Task CompletingASingleBookingStillCollects()
    {
        // The other path deliberately DOES settle — at the counter, "he played" and "he
        // paid" are one moment. The asymmetry between the two is intentional, so pin it.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await PastUnpaidBooking(venue.Id, "20:00");
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await owner.PatchAsync($"/api/v1/bookings/{booking.Id}/complete", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var after = await _fx.LoadBooking(booking.Id);
        Assert.Equal(20, after!.AmountPaid);
        Assert.Single(await _fx.LoadPayments(booking.Id));
    }

    [Fact]
    public async Task SettlingTwiceWritesOneRow()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await PastUnpaidBooking(venue.Id, "21:00");
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        await owner.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { booking.Id } });
        await owner.PatchAsync($"/api/v1/bookings/{booking.Id}/mark-paid", null);
        await owner.PatchAsync($"/api/v1/bookings/{booking.Id}/mark-paid", null);

        Assert.Single(await _fx.LoadPayments(booking.Id));
    }
}
