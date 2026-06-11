using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.PermanentBookings;

[Collection("Api")]
public class PermanentBookingCapacityTests
{
    private readonly DatabaseFixture _fx;

    public PermanentBookingCapacityTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private static DateTime FutureDate => PlatformConstants.JordanToday().AddDays(7);

    private static object Body(int dayOfWeek, string startTime = "10:00", int duration = 60,
        string? pitchSize = null) => new
    {
        pitchSize,
        dayOfWeek,
        startTime,
        duration
    };

    private static Task<HttpResponseMessage> CreatePermanent(HttpClient client, string venueId, object body) =>
        client.PostAsJsonAsync($"/api/v1/venues/{venueId}/permanent-bookings", body);

    [Fact]
    public async Task Create_ByNonOwningOwner_Returns403()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerCId, "venue_owner");

        var res = await CreatePermanent(client, venue.Id, Body((int)FutureDate.DayOfWeek));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task OverlappingPermanent_OnNonSubdividablePitch_Returns409()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var dow = (int)FutureDate.DayOfWeek;

        var first = await CreatePermanent(client, venue.Id, Body(dow, "10:00"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await CreatePermanent(client, venue.Id, Body(dow, "10:30"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("already reserved", body!.Message);
    }

    [Fact]
    public async Task Subdividable_CapacityArithmetic()
    {
        var (venue, _) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var dow = (int)FutureDate.DayOfWeek;

        // Two 8-aside permanents (2 units each) fill the 4-unit pitch.
        var first = await CreatePermanent(client, venue.Id, Body(dow, pitchSize: "8"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await CreatePermanent(client, venue.Id, Body(dow, pitchSize: "8"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var third = await CreatePermanent(client, venue.Id, Body(dow, pitchSize: "6"));
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

        var body = await third.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("0 of 4 units remain", body!.Message);
    }

    [Fact]
    public async Task Permanent_BlocksSubsequentOneOffBooking()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var created = await CreatePermanent(owner, venue.Id, Body((int)FutureDate.DayOfWeek));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await player.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = FutureDate.ToString("yyyy-MM-dd"),
            startTime = "10:00",
            duration = 60,
            paymentMethod = "cliq"
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Empty(await _fx.LoadVenueBookings(venue.Id));
    }

    [Fact]
    public async Task OutsideOperatingHours_Returns400()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await CreatePermanent(client, venue.Id, Body((int)FutureDate.DayOfWeek, "06:00"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("operating hours", body!.Message);
    }
}
