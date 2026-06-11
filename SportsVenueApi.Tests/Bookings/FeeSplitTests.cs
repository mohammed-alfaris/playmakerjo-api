using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

[Collection("Api")]
public class FeeSplitTests
{
    private readonly DatabaseFixture _fx;

    public FeeSplitTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(7).ToString("yyyy-MM-dd");

    private static object Body(string venueId, int duration = 60, string sport = "basketball",
        string? pitchSize = null, string paymentMethod = "cliq", bool isManual = false) => new
    {
        venueId,
        sport,
        pitchSize,
        date = FutureDate,
        startTime = "10:00",
        duration,
        paymentMethod,
        isManual
    };

    [Fact]
    public async Task NinetyMinutes_At20PerHour_SplitsAmounts()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, duration: 90));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        var dto = body!.Data!;
        Assert.Equal(30, dto.TotalAmount, 3);
        Assert.Equal(6, dto.DepositAmount, 3);
        Assert.Equal(0, dto.AmountPaid, 3);
        Assert.False(dto.DepositPaid);
        Assert.Equal("pending_payment", dto.Status);

        var row = await _fx.LoadBooking(dto.Id);
        Assert.Equal(30, row!.TotalAmount, 3);
        Assert.Equal(6, row.DepositAmount, 3);
        Assert.Equal(1.5, row.SystemFee, 3);
        Assert.Equal(28.5, row.OwnerAmount, 3);
        Assert.Equal(5, row.SystemFeePercentage, 3);
    }

    [Fact]
    public async Task ManualBooking_ByVenueOwner_NoFeeAndConfirmed()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await client.PostAsJsonAsync("/api/v1/bookings",
            Body(venue.Id, paymentMethod: "cash", isManual: true));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        var dto = body!.Data!;
        Assert.Equal("confirmed", dto.Status);
        Assert.True(dto.DepositPaid);
        Assert.Equal(dto.TotalAmount, dto.AmountPaid, 3);

        var row = await _fx.LoadBooking(dto.Id);
        Assert.Equal(0, row!.SystemFee, 3);
        Assert.Equal(0, row.SystemFeePercentage, 3);
        Assert.Equal(row.TotalAmount, row.OwnerAmount, 3);
        Assert.Equal(row.TotalAmount, row.AmountPaid, 3);
    }

    [Fact]
    public async Task ManualBooking_ByPlayer_Returns403()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings",
            Body(venue.Id, paymentMethod: "cash", isManual: true));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        Assert.Empty(await _fx.LoadVenueBookings(venue.Id));
    }

    [Fact]
    public async Task SizePrice_BeatsPitchPrice()
    {
        var (venue, _) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        // sizePrices["8"] = 20 wins over pitch.pricePerHour = 40.
        var res = await client.PostAsJsonAsync("/api/v1/bookings",
            Body(venue.Id, sport: "football", pitchSize: "8"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        Assert.Equal(20, body!.Data!.TotalAmount, 3);
    }

    [Fact]
    public async Task PitchPrice_BeatsVenuePrice()
    {
        // 11-aside has no sizePrices entry → pitch.pricePerHour = 40 wins over venue 99.
        var (venue, _) = await _fx.CreateSubdividableVenue(_fx.OwnerAId, v => v.PricePerHour = 99);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings",
            Body(venue.Id, sport: "football", pitchSize: "11"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        Assert.Equal(40, body!.Data!.TotalAmount, 3);
    }

    [Fact]
    public async Task VenuePrice_UsedWhenPitchPriceUnset()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.Pitches =
        [
            new PitchDto { Id = "court-1", Name = "Court 1", Sport = "basketball", PricePerHour = 0 }
        ]);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        Assert.Equal(20, body!.Data!.TotalAmount, 3);
    }
}
