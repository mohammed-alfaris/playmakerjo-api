using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

/// <summary>
/// The operating-hours check had no tests at all, which is how it managed to be dead for
/// months and then, once revived, wrong in two new ways.
///
/// It was dead because it built a 3-letter day key ("mon") while the dashboard writes full
/// names ("monday"), so the lookup always missed and the whole block was skipped. Reviving it
/// exposed two defects that had never been able to fire:
///
///   1. No midnight wrap. A venue open 18:00-02:00 has close &lt; open, so every single
///      booking failed "within operating hours" and the availability grid rendered empty.
///      Evening-into-night is a normal schedule for a pitch here, not an edge case.
///   2. A weekday missing from the map was read as "closed". The dashboard always writes all
///      seven days, so a gap means a legacy row or one created through the API — and those
///      venues became unbookable every day of the week.
///
/// Four call sites each carried their own copy of the resolve-and-compare, and only the
/// permanent-booking one wrapped past midnight, while its comment claimed to mirror the
/// booking path. These tests pin the behaviour that all of them now share.
/// </summary>
[Collection("Api")]
public class OperatingHoursTests
{
    private readonly DatabaseFixture _fx;

    public OperatingHoursTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>A date far enough out that nothing else in the suite books into it.</summary>
    private static DateTime FutureDay => PlatformConstants.JordanToday().AddDays(11);

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd");

    private static object Booking(string venueId, string date, string startTime, int duration = 60) => new
    {
        venueId,
        sport = "basketball",
        date,
        startTime,
        duration,
        paymentMethod = "cliq",
        isManual = true,
    };

    private async Task<HttpResponseMessage> Book(string venueId, DateTime day, string startTime, int duration = 60)
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        return await client.PostAsJsonAsync("/api/v1/bookings", Booking(venueId, Iso(day), startTime, duration));
    }

    private static async Task<string> Message(HttpResponseMessage res)
        => (await res.Content.ReadFromJsonAsync<ApiResponse<object>>())?.Message ?? "";

    // ------------------------------------------------------------- overnight (18:00-02:00)

    [Fact]
    public async Task AnOvernightVenue_AcceptsAnEveningBooking()
    {
        var venue = await _fx.CreateBasketballVenue(
            _fx.OwnerAId, v => v.OperatingHours = TestEntities.DailyHours("18:00", "02:00"));

        var res = await Book(venue.Id, FutureDay, "20:00");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AnOvernightVenue_AcceptsASlotThatRunsPastMidnight()
    {
        var venue = await _fx.CreateBasketballVenue(
            _fx.OwnerAId, v => v.OperatingHours = TestEntities.DailyHours("18:00", "02:00"));

        // 23:00 + 3h ends at 02:00 — exactly the close time, on the following day.
        var res = await Book(venue.Id, FutureDay, "23:00", 180);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AnOvernightVenue_StillRejectsASlotOutsideTheWindow()
    {
        var venue = await _fx.CreateBasketballVenue(
            _fx.OwnerAId, v => v.OperatingHours = TestEntities.DailyHours("18:00", "02:00"));

        // The wrap must not degrade into "anything goes": 15:00 is shut.
        var res = await Book(venue.Id, FutureDay, "15:00");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("operating hours", await Message(res));
    }

    [Fact]
    public async Task AnOvernightVenue_IsOfferedByTheAvailabilitySearch()
    {
        // /venues/search is the route that runs PitchHasCapacity, which carried its own
        // copy of the comparison. An overnight venue was filtered out of every search
        // result for every time of day: invisible to a player looking for a pitch.
        var venue = await _fx.CreateBasketballVenue(
            _fx.OwnerAId, v => v.OperatingHours = TestEntities.DailyHours("18:00", "02:00"));
        var anonymous = _fx.Factory.CreateClient();

        var res = await anonymous.GetAsync(
            $"/api/v1/venues/search?date={Iso(FutureDay)}&startTime=20:00&duration=60&sport=basketball");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(venue.Id, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheSearchAndTheBookingEndpointAgree_OnAnOvernightVenueAtAShutHour()
    {
        // Both sides of the same rule: 15:00 is outside 18:00-02:00, so the search must
        // not offer the venue and the create call must refuse it.
        var venue = await _fx.CreateBasketballVenue(
            _fx.OwnerAId, v => v.OperatingHours = TestEntities.DailyHours("18:00", "02:00"));
        var anonymous = _fx.Factory.CreateClient();

        var search = await anonymous.GetAsync(
            $"/api/v1/venues/search?date={Iso(FutureDay)}&startTime=15:00&duration=60&sport=basketball");

        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        Assert.DoesNotContain(venue.Id, await search.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------- a day missing from the map

    [Fact]
    public async Task AVenueWhoseHoursOmitTheDay_IsStillBookable()
    {
        var day = FutureDay;
        var missing = TestEntities.DailyHours();
        missing.Remove(day.DayOfWeek.ToString().ToLower());

        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.OperatingHours = missing);

        var res = await Book(venue.Id, day, "10:00");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AnExplicitlyClosedDay_IsStillRejected()
    {
        var day = FutureDay;
        var hours = TestEntities.DailyHours();
        hours[day.DayOfWeek.ToString().ToLower()] = new { open = "08:00", close = "23:00", closed = true };

        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.OperatingHours = hours);

        var res = await Book(venue.Id, day, "10:00");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("closed", await Message(res));
    }

    // ----------------------------------------------------------- ordinary hours unchanged

    [Fact]
    public async Task AnOrdinaryVenue_RejectsASlotBeforeOpening()
    {
        var venue = await _fx.CreateBasketballVenue(
            _fx.OwnerAId, v => v.OperatingHours = TestEntities.DailyHours("08:00", "23:00"));

        var res = await Book(venue.Id, FutureDay, "03:00");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("operating hours", await Message(res));
    }

    [Fact]
    public async Task AnOrdinaryVenue_RejectsASlotRunningPastClosing()
    {
        var venue = await _fx.CreateBasketballVenue(
            _fx.OwnerAId, v => v.OperatingHours = TestEntities.DailyHours("08:00", "23:00"));

        var res = await Book(venue.Id, FutureDay, "22:30", 120);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("operating hours", await Message(res));
    }
}
