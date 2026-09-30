using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.PermanentBookings;
using SportsVenueApi.DTOs.Reports;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Money;

/// <summary>
/// Blocked time is refused by every door a slot can be sold through — counter, app, move,
/// recurring series, recorded standing week — shown as taken by availability and search,
/// and left out of the open time that occupancy is measured against.
/// </summary>
[Collection("Api")]
public class VenueBlockTests
{
    private readonly DatabaseFixture _fx;

    public VenueBlockTests(DatabaseFixture fx) => _fx = fx;

    private static DateTime DayOf(int ahead) => PlatformConstants.JordanToday().AddDays(ahead);
    private static string Day(int ahead) => DayOf(ahead).ToString("yyyy-MM-dd");

    private sealed record Co(User Owner, HttpClient Client, Venue Venue);

    private async Task<Co> NewCompany(bool twoCourts = false)
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id, v =>
        {
            if (twoCourts)
                v.Pitches =
                [
                    new PitchDto { Id = "c1-" + Guid.NewGuid().ToString("N")[..6], Name = "Court 1", Sport = "basketball", PricePerHour = 20 },
                    new PitchDto { Id = "c2-" + Guid.NewGuid().ToString("N")[..6], Name = "Court 2", Sport = "basketball", PricePerHour = 20 },
                ];
        });
        return new Co(owner, _fx.CreateClientFor(owner.Id, "venue_owner"), venue);
    }

    private static async Task<CreateVenueBlockResponse> Block(Co co, int daysAhead, string from, string to, string? pitchId = null, string reason = "Maintenance")
    {
        var res = await co.Client.PostAsJsonAsync($"/api/v1/venues/{co.Venue.Id}/blocks", new
        {
            pitchId, startsAt = $"{Day(daysAhead)}T{from}", endsAt = $"{Day(daysAhead)}T{to}", reason,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<CreateVenueBlockResponse>>())!.Data!;
    }

    private static Task<HttpResponseMessage> Counter(Co co, int daysAhead, string start, string? pitchId = null, int duration = 60) =>
        co.Client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = co.Venue.Id, sport = "basketball", pitchId, date = Day(daysAhead), startTime = start, duration,
            paymentMethod = "cliq", isManual = true,
            customerPhone = "0791234" + Random.Shared.Next(100, 999), customerName = "Block Test",
        });

    [Fact]
    public async Task ABlock_RefusesTheCounterAndTheApp_ButItsEndIsFree()
    {
        var co = await NewCompany();
        await Block(co, 3, "18:00", "22:00");

        var counter = await Counter(co, 3, "21:00");
        Assert.Equal(HttpStatusCode.Conflict, counter.StatusCode);
        Assert.Contains("Maintenance", (await counter.Content.ReadFromJsonAsync<ApiResponse<object>>())!.Message);

        var app = await _fx.CreateClientFor(_fx.PlayerId, "player").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = co.Venue.Id, sport = "basketball", date = Day(3), startTime = "17:30", duration = 60, paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.Conflict, app.StatusCode);

        // Exclusive end: 22:00 is open again, as is anything before 18:00.
        Assert.Equal(HttpStatusCode.OK, (await Counter(co, 3, "22:00")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Counter(co, 3, "17:00")).StatusCode);
    }

    [Fact]
    public async Task ABookingCannotBeMovedIntoABlock()
    {
        var co = await NewCompany();
        var id = (await (await Counter(co, 3, "10:00")).Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
        await Block(co, 3, "18:00", "22:00");

        var res = await co.Client.PatchAsJsonAsync($"/api/v1/bookings/{id}", new { startTime = "19:00" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task APitchBlock_ClosesOnlyThatPitch_AndAVenueBlock_ClosesThemAll()
    {
        var co = await NewCompany(twoCourts: true);
        var (c1, c2) = (co.Venue.Pitches[0].Id, co.Venue.Pitches[1].Id);
        await Block(co, 3, "18:00", "20:00", pitchId: c1);
        await Block(co, 4, "18:00", "20:00");

        Assert.Equal(HttpStatusCode.Conflict, (await Counter(co, 3, "18:00", c1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Counter(co, 3, "18:00", c2)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Counter(co, 4, "18:00", c1)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Counter(co, 4, "18:00", c2)).StatusCode);
    }

    [Fact]
    public async Task AvailabilityShowsTheBlockAsTaken()
    {
        var co = await NewCompany(twoCourts: true);
        var c1 = co.Venue.Pitches[0].Id;
        await Block(co, 3, "18:00", "20:00", pitchId: c1);

        var res = await _fx.CreateClientFor(_fx.PlayerId, "player")
            .GetAsync($"/api/v1/venues/{co.Venue.Id}/available-slots?date={Day(3)}");
        var data = (await res.Content.ReadFromJsonAsync<ApiResponse<AvailableSlotsResponse>>())!.Data!;

        var court1 = data.Pitches!.Single(p => p.PitchId == c1);
        var blocked = Assert.Single(court1.BookedSlots, s => s.Blocked);
        Assert.Equal(("18:00", 120, 1), (blocked.StartTime, blocked.Duration, blocked.UnitWeight));
        Assert.DoesNotContain(data.Pitches!.Single(p => p.PitchId != c1).BookedSlots, s => s.Blocked);
    }

    [Fact]
    public async Task SearchLeavesOutAVenueThatIsBlocked()
    {
        var co = await NewCompany();
        await Block(co, 3, "08:00", "23:00", reason: "Eid");

        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        async Task<bool> Listed(int day) =>
            (await (await player.GetAsync($"/api/v1/venues/search?date={Day(day)}&startTime=19:00&duration=60&sport=basketball&limit=50"))
                .Content.ReadFromJsonAsync<ApiResponse<List<VenueResponse>>>())!.Data!.Any(v => v.Id == co.Venue.Id);

        // A new company's venue is the newest, so it is on the first page when available.
        Assert.False(await Listed(3));
        Assert.True(await Listed(5));
    }

    [Fact]
    public async Task ARecurringSeries_SkipsTheBlockedWeek_OrFailsOnIt()
    {
        var co = await NewCompany();
        await Block(co, 14, "08:00", "23:00");
        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        object Series(string policy) => new
        {
            venueId = co.Venue.Id, sport = "basketball", startDate = Day(7), endDate = Day(21), startTime = "10:00",
            duration = 60, recurrenceType = "weekly", paymentMethod = "cliq", conflictPolicy = policy,
        };

        Assert.Equal(HttpStatusCode.Conflict, (await player.PostAsJsonAsync("/api/v1/bookings/recurring", Series("fail"))).StatusCode);

        var skip = await player.PostAsJsonAsync("/api/v1/bookings/recurring", Series("skip"));
        var created = (await skip.Content.ReadFromJsonAsync<ApiResponse<RecurringBookingResponse>>())!.Data!;
        Assert.Equal(new[] { Day(7), Day(21) }, created.Created.Select(b => b.Date[..10]).OrderBy(d => d).ToArray());
    }

    [Fact]
    public async Task AStandingWeekInsideABlock_CannotBeRecorded()
    {
        var co = await NewCompany();
        var res = await co.Client.PostAsJsonAsync($"/api/v1/venues/{co.Venue.Id}/permanent-bookings", new
        {
            dayOfWeek = (int)DayOf(7).DayOfWeek, startTime = "20:00", duration = 60, label = "Thursday regulars",
        });
        var standingId = (await res.Content.ReadFromJsonAsync<ApiResponse<PermanentBookingDto>>())!.Data!.Id;
        await Block(co, 7, "19:00", "23:00");

        var record = await co.Client.PostAsJsonAsync($"/api/v1/permanent-bookings/{standingId}/record", new { date = Day(7) });
        var nextWeek = await co.Client.PostAsJsonAsync($"/api/v1/permanent-bookings/{standingId}/record", new { date = Day(14) });

        Assert.Equal(HttpStatusCode.Conflict, record.StatusCode);
        Assert.Equal(HttpStatusCode.OK, nextWeek.StatusCode);
    }

    [Fact]
    public async Task BlockingOverBookings_ListsThem_AndRemovingTheBlockFreesTheSlot()
    {
        var co = await NewCompany();
        var inside = (await (await Counter(co, 3, "19:00")).Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
        await Counter(co, 3, "10:00");

        var created = await Block(co, 3, "18:00", "22:00");

        Assert.Equal(inside, Assert.Single(created.OverlappingBookings).Id);
        Assert.Equal("confirmed", (await _fx.LoadBooking(inside))!.Status); // listed, not cancelled

        Assert.Equal(HttpStatusCode.Conflict, (await Counter(co, 3, "20:00")).StatusCode);
        var del = await co.Client.DeleteAsync($"/api/v1/venues/{co.Venue.Id}/blocks/{created.Block.Id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Counter(co, 3, "20:00")).StatusCode);
    }

    [Fact]
    public async Task OnlyThoseWhoManageBookings_CanBlockTime()
    {
        var co = await NewCompany();
        var viewer = await _fx.ClerkWith(co.Client, StaffPermissions.BookingsView);
        var body = new { startsAt = $"{Day(3)}T18:00", endsAt = $"{Day(3)}T20:00" };

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/v1/venues/{co.Venue.Id}/blocks", body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/v1/venues/{co.Venue.Id}/blocks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await (await NewCompany()).Client.PostAsJsonAsync($"/api/v1/venues/{co.Venue.Id}/blocks", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await co.Client.PostAsJsonAsync($"/api/v1/venues/{co.Venue.Id}/blocks",
            new { startsAt = $"{Day(3)}T20:00", endsAt = $"{Day(3)}T18:00" })).StatusCode);
    }

    [Fact]
    public async Task BlockedTime_IsNotCountedAsOpen()
    {
        var co = await NewCompany();
        await Block(co, 3, "18:00", "22:00");

        var res = await co.Client.GetAsync($"/api/v1/reports/occupancy?from={Day(3)}&to={Day(3)}&venue_id={co.Venue.Id}");
        var report = (await res.Content.ReadFromJsonAsync<ApiResponse<OccupancyReport>>())!.Data!;

        Assert.Equal(15 - 4, report.OpenHours, 2); // open 08:00–23:00, blocked 4h
    }
}
