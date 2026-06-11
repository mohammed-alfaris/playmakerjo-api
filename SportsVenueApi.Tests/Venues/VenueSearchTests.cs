using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Venues;

[Collection("Api")]
public class VenueSearchTests
{
    private readonly DatabaseFixture _fx;

    public VenueSearchTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private static DateTime FutureDate => PlatformConstants.JordanToday().AddDays(14);

    private static string UniqueCity => "SearchCity-" + Guid.NewGuid().ToString("N")[..6];

    private static string SearchUrl(string date, string startTime, int duration = 60,
        string? sport = null, string? city = null, int limit = 50)
    {
        var url = $"/api/v1/venues/search?date={date}&startTime={startTime}&duration={duration}&limit={limit}";
        if (sport != null) url += $"&sport={sport}";
        if (city != null) url += $"&city={city}";
        return url;
    }

    private async Task<List<VenueResponse>> Search(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<List<VenueResponse>>>();
        Assert.True(body!.Success);
        Assert.NotNull(body.Pagination);
        return body.Data!;
    }

    [Fact]
    public async Task Anonymous_FreeSlot_IncludesSeededVenue()
    {
        var client = _fx.Factory.CreateClient();
        var venues = await Search(client, SearchUrl(FutureDate.ToString("yyyy-MM-dd"), "09:00", city: "Amman"));
        Assert.Contains(venues, v => v.Id == _fx.VenueAId);
    }

    [Fact]
    public async Task FullyBookedVenue_IsExcluded()
    {
        var city = UniqueCity;
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.City = city);
        await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = _fx.PlayerId, Sport = "basketball",
            Date = FutureDate, StartTime = "09:00", Duration = 60, Status = "confirmed"
        });

        var client = _fx.Factory.CreateClient();
        var date = FutureDate.ToString("yyyy-MM-dd");
        Assert.DoesNotContain(await Search(client, SearchUrl(date, "09:00", city: city)), v => v.Id == venue.Id);
        Assert.Contains(await Search(client, SearchUrl(date, "10:00", city: city)), v => v.Id == venue.Id);
    }

    [Fact]
    public async Task ClosedDay_IsExcluded()
    {
        var city = UniqueCity;
        var hours = TestEntities.DailyHours();
        hours[FutureDate.DayOfWeek.ToString().ToLower()] = new { closed = true };
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v =>
        {
            v.City = city;
            v.OperatingHours = hours;
        });

        var client = _fx.Factory.CreateClient();
        var venues = await Search(client, SearchUrl(FutureDate.ToString("yyyy-MM-dd"), "09:00", city: city));
        Assert.DoesNotContain(venues, v => v.Id == venue.Id);
    }

    [Fact]
    public async Task SportFilter_ExcludesNonMatchingVenues()
    {
        var client = _fx.Factory.CreateClient();
        var venues = await Search(client,
            SearchUrl(FutureDate.ToString("yyyy-MM-dd"), "09:00", sport: "football", city: "Amman"));
        Assert.Contains(venues, v => v.Id == _fx.VenueSubId);
        Assert.DoesNotContain(venues, v => v.Id == _fx.VenueAId);
    }

    [Fact]
    public async Task InvalidDate_Returns400()
    {
        var client = _fx.Factory.CreateClient();

        var res = await client.GetAsync("/api/v1/venues/search?date=not-a-date&startTime=09:00");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.False(body!.Success);

        var noTime = await client.GetAsync($"/api/v1/venues/search?date={FutureDate:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.BadRequest, noTime.StatusCode);
    }

    [Fact]
    public async Task SearchRoute_IsNotShadowedByVenueIdRoute()
    {
        // If "search" were captured by GET /venues/{venueId}, the [Authorize]'d
        // Get action would answer 401 for an anonymous caller — Search answers
        // 400 for the missing date.
        var client = _fx.Factory.CreateClient();
        var res = await client.GetAsync("/api/v1/venues/search");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("date", body!.Message);
    }

    [Fact]
    public async Task Subdividable_IncludedWhileCapacityRemains()
    {
        var city = UniqueCity;
        var (venue, pitchId) = await _fx.CreateSubdividableVenue(_fx.OwnerAId, v => v.City = city);
        await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = _fx.PlayerId, Sport = "football",
            PitchId = pitchId, PitchSize = "8",
            Date = FutureDate, StartTime = "09:00", Duration = 60, Status = "confirmed"
        });

        var client = _fx.Factory.CreateClient();
        var date = FutureDate.ToString("yyyy-MM-dd");
        var url = SearchUrl(date, "09:00", sport: "football", city: city);

        // 8-aside uses 2 of 4 units — the pitch still fits the smallest size.
        Assert.Contains(await Search(client, url), v => v.Id == venue.Id);

        await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = _fx.PlayerId, Sport = "football",
            PitchId = pitchId, PitchSize = "8",
            Date = FutureDate, StartTime = "09:00", Duration = 60, Status = "confirmed"
        });

        // 4 of 4 units used — nothing fits anymore.
        Assert.DoesNotContain(await Search(client, url), v => v.Id == venue.Id);
    }
}
