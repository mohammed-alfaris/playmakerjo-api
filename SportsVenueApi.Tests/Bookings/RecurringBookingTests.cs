using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

[Collection("Api")]
public class RecurringBookingTests
{
    private readonly DatabaseFixture _fx;

    public RecurringBookingTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private static DateTime StartDate => PlatformConstants.JordanToday().AddDays(7);

    private static object Body(string venueId, DateTime startDate, DateTime endDate,
        string recurrenceType = "weekly", string conflictPolicy = "skip") => new
    {
        venueId,
        sport = "basketball",
        startDate = startDate.ToString("yyyy-MM-dd"),
        endDate = endDate.ToString("yyyy-MM-dd"),
        startTime = "10:00",
        duration = 60,
        recurrenceType,
        paymentMethod = "cliq",
        conflictPolicy
    };

    private async Task BookOneOff(string venueId, DateTime date)
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId,
            sport = "basketball",
            date = date.ToString("yyyy-MM-dd"),
            startTime = "10:00",
            duration = 60,
            paymentMethod = "cliq"
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Weekly_FourWeeks_CreatesFourBookingsSharingGroup()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings/recurring",
            Body(venue.Id, StartDate, StartDate.AddDays(21)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<RecurringBookingResponse>>();
        var data = body!.Data!;
        Assert.Equal(4, data.RequestedCount);
        Assert.Equal(4, data.Created.Count);
        Assert.Empty(data.SkippedDates);
        Assert.All(data.Created, b => Assert.Equal(data.GroupId, b.RecurringGroupId));

        var rows = await _fx.LoadVenueBookings(venue.Id);
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(data.GroupId, r.RecurringGroupId));
    }

    [Fact]
    public async Task ConflictWithSkipPolicy_SkipsConflictedDate()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var conflictedDate = StartDate.AddDays(7);
        await BookOneOff(venue.Id, conflictedDate);

        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings/recurring",
            Body(venue.Id, StartDate, StartDate.AddDays(21)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<RecurringBookingResponse>>();
        var data = body!.Data!;
        Assert.Equal(4, data.RequestedCount);
        Assert.Equal(3, data.Created.Count);
        Assert.Equal(new[] { conflictedDate.ToString("yyyy-MM-dd") }, data.SkippedDates);
        Assert.DoesNotContain(data.Created, b => b.Date == conflictedDate.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public async Task ConflictWithFailPolicy_Returns409AndPersistsNothing()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var conflictedDate = StartDate.AddDays(7);
        await BookOneOff(venue.Id, conflictedDate);

        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings/recurring",
            Body(venue.Id, StartDate, StartDate.AddDays(21), conflictPolicy: "fail"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        var raw = await res.Content.ReadAsStringAsync();
        Assert.Contains(conflictedDate.ToString("yyyy-MM-dd"), raw);

        var rows = await _fx.LoadVenueBookings(venue.Id);
        var single = Assert.Single(rows);
        Assert.Null(single.RecurringGroupId);
    }

    [Fact]
    public async Task AllDatesConflicting_Returns400()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        await BookOneOff(venue.Id, StartDate);
        await BookOneOff(venue.Id, StartDate.AddDays(7));

        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings/recurring",
            Body(venue.Id, StartDate, StartDate.AddDays(7)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("All occurrences conflict", body!.Message);
        Assert.Equal(2, (await _fx.LoadVenueBookings(venue.Id)).Count);
    }

    [Fact]
    public async Task RangeOverThreeMonths_Returns400()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings/recurring",
            Body(venue.Id, StartDate, StartDate.AddMonths(3).AddDays(1)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("3 months", body!.Message);
        Assert.Empty(await _fx.LoadVenueBookings(venue.Id));
    }

    [Fact]
    public async Task Biweekly_UsesFourteenDayStride()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings/recurring",
            Body(venue.Id, StartDate, StartDate.AddDays(28), recurrenceType: "biweekly"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<RecurringBookingResponse>>();
        var dates = body!.Data!.Created.Select(b => b.Date).ToList();
        Assert.Equal(new[]
        {
            StartDate.ToString("yyyy-MM-dd"),
            StartDate.AddDays(14).ToString("yyyy-MM-dd"),
            StartDate.AddDays(28).ToString("yyyy-MM-dd")
        }, dates);
    }
}
