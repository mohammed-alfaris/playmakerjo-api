using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

[Collection("Api")]
public class BookingOverlapTests
{
    private readonly DatabaseFixture _fx;

    public BookingOverlapTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private static DateTime FutureDate => PlatformConstants.JordanToday().AddDays(7);

    private static object Body(string venueId, string date, string startTime, int duration = 60,
        string sport = "basketball", string? pitchId = null, string? pitchSize = null) => new
    {
        venueId,
        sport,
        pitchId,
        pitchSize,
        date,
        startTime,
        duration,
        paymentMethod = "cliq"
    };

    [Fact]
    public async Task ExactOverlap_Returns409()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var date = FutureDate.ToString("yyyy-MM-dd");

        var first = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.False(body!.Success);
        Assert.Contains("conflicts", body.Message);
    }

    [Fact]
    public async Task PartialOverlap_Returns409()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var date = FutureDate.ToString("yyyy-MM-dd");

        var first = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", duration: 90));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "11:00"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task BackToBack_Succeeds()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var date = FutureDate.ToString("yyyy-MM-dd");

        var first = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "11:00"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task CancelledBooking_DoesNotBlockSlot()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var date = FutureDate.ToString("yyyy-MM-dd");

        var first = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();

        var cancel = await client.PatchAsync($"/api/v1/bookings/{created!.Data!.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task DifferentPitch_SameTime_Succeeds()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.Pitches =
        [
            new PitchDto { Id = "court-1", Name = "Court 1", Sport = "basketball", PricePerHour = 20 },
            new PitchDto { Id = "court-2", Name = "Court 2", Sport = "basketball", PricePerHour = 20 }
        ]);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var date = FutureDate.ToString("yyyy-MM-dd");

        var first = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", pitchId: "court-1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", pitchId: "court-2"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task Subdividable_FullCapacity_NextSizeRejected()
    {
        var (venue, _) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var date = FutureDate.ToString("yyyy-MM-dd");

        // 8-aside weighs 2 units; two of them fill the 4-unit 11-aside pitch.
        var first = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", sport: "football", pitchSize: "8"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", sport: "football", pitchSize: "8"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var third = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", sport: "football", pitchSize: "6"));
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

        var body = await third.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("0 of 4 units remain", body!.Message);
    }

    [Fact]
    public async Task Subdividable_SubSizeFits_WhileUnitsRemain()
    {
        var (venue, _) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var date = FutureDate.ToString("yyyy-MM-dd");

        // 2 + 1 + 1 units fit exactly into capacity 4; the next unit is rejected.
        var first = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", sport: "football", pitchSize: "8"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", sport: "football", pitchSize: "6"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var third = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", sport: "football", pitchSize: "6"));
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);

        var fourth = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date, "10:00", sport: "football", pitchSize: "6"));
        Assert.Equal(HttpStatusCode.Conflict, fourth.StatusCode);
    }

    [Fact]
    public async Task Permanent_BlocksSameWeekday_SamePitch()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var date = FutureDate;
        await _fx.Insert(new PermanentBooking
        {
            VenueId = venue.Id, Sport = "basketball", DayOfWeek = (int)date.DayOfWeek,
            StartTime = "10:00", Duration = 60, Status = "active", CreatedByUserId = _fx.OwnerAId
        });

        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, date.ToString("yyyy-MM-dd"), "10:00"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("recurring reservation", body!.Message);
    }

    [Fact]
    public async Task Permanent_DoesNotBlockOtherWeekdays()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var date = FutureDate;
        await _fx.Insert(new PermanentBooking
        {
            VenueId = venue.Id, Sport = "basketball", DayOfWeek = (int)date.DayOfWeek,
            StartTime = "10:00", Duration = 60, Status = "active", CreatedByUserId = _fx.OwnerAId
        });

        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings",
            Body(venue.Id, date.AddDays(1).ToString("yyyy-MM-dd"), "10:00"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
