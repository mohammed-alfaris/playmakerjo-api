using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

/// <summary>
/// Nothing in the system can observe whether a customer turned up — there is no gate, no
/// check-in, no sensor. The only source of truth is a human saying so, and left to itself
/// nobody ever taps "no-show", because there is no reason to when everything went fine.
///
/// This endpoint surfaces the slots that have already happened but nobody has ruled on, so
/// the owner can mark the exceptions. Everything it returns is assumed attended until told
/// otherwise: the safe failure is silence, never a wrongly-accused customer.
/// </summary>
[Collection("Api")]
public class AttendanceReviewTests
{
    private readonly DatabaseFixture _fx;

    public AttendanceReviewTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>Inserts a booking directly — POST /bookings rightly refuses past dates.</summary>
    private async Task<Booking> SeedBooking(string venueId, int daysAgo, string status, string startTime = "10:00")
    {
        return await _fx.Insert(new Booking
        {
            VenueId = venueId,
            PlayerId = _fx.PlayerId,
            Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(-daysAgo),
            StartTime = startTime,
            Duration = 60,
            Amount = 20,
            TotalAmount = 20,
            Status = status,
        });
    }

    private static async Task<List<BookingResponse>> Pending(HttpClient client, int days = 7)
    {
        var res = await client.GetAsync($"/api/v1/bookings/attendance-pending?days={days}");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<List<BookingResponse>>>();
        return body!.Data!;
    }

    [Fact]
    public async Task Route_IsNotShadowedByBookingIdRoute()
    {
        // "attendance-pending" would otherwise be read as an {id}. The venue search route
        // hit exactly this trap once already, so it is worth pinning.
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/bookings/attendance-pending");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task ReturnsPastConfirmedBookings_ForTheOwnersOwnVenues()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var past = await SeedBooking(venue.Id, daysAgo: 1, "confirmed", "11:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var pending = await Pending(client);

        Assert.Contains(pending, b => b.Id == past.Id);
    }

    [Fact]
    public async Task ExcludesTodayAndFuture()
    {
        // A slot later this afternoon has not happened yet. Asking about it would train the
        // owner to answer before he knows.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var today = await SeedBooking(venue.Id, daysAgo: 0, "confirmed", "12:00");
        var future = await SeedBooking(venue.Id, daysAgo: -3, "confirmed", "13:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var pending = await Pending(client);

        Assert.DoesNotContain(pending, b => b.Id == today.Id);
        Assert.DoesNotContain(pending, b => b.Id == future.Id);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("no_show")]
    [InlineData("cancelled")]
    [InlineData("pending_payment")]
    public async Task ExcludesBookingsAlreadyRuledOn(string status)
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var settled = await SeedBooking(venue.Id, daysAgo: 2, status, "14:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var pending = await Pending(client);

        Assert.DoesNotContain(pending, b => b.Id == settled.Id);
    }

    [Fact]
    public async Task ExcludesCompetitorsBookings()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var theirs = await SeedBooking(venueB.Id, daysAgo: 1, "confirmed", "15:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var pending = await Pending(client);

        Assert.DoesNotContain(pending, b => b.Id == theirs.Id);
    }

    [Fact]
    public async Task RespectsTheDayWindow()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var ancient = await SeedBooking(venue.Id, daysAgo: 20, "confirmed", "16:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var pending = await Pending(client, days: 7);

        Assert.DoesNotContain(pending, b => b.Id == ancient.Id);
    }

    [Fact]
    public async Task Player_IsRefused()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.GetAsync("/api/v1/bookings/attendance-pending");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task UnlinkedStaff_IsRefused()
    {
        var client = _fx.CreateClientFor(_fx.StaffUnlinkedId, "venue_staff");
        var res = await client.GetAsync("/api/v1/bookings/attendance-pending");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ------------------------------------------------------------------- confirming

    [Fact]
    public async Task Confirm_MarksThemCompleted()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var a = await SeedBooking(venue.Id, daysAgo: 1, "confirmed", "17:00");
        var b = await SeedBooking(venue.Id, daysAgo: 1, "confirmed", "18:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { a.Id, b.Id } });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("completed", (await _fx.LoadBooking(a.Id))!.Status);
        Assert.Equal("completed", (await _fx.LoadBooking(b.Id))!.Status);
    }

    [Fact]
    public async Task Confirm_SilentlySkipsCompetitorsBookings()
    {
        // The ids come from the client. A stale tab must not become a way to write into
        // someone else's venue.
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var theirs = await SeedBooking(venueB.Id, daysAgo: 1, "confirmed", "19:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { theirs.Id } });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("confirmed", (await _fx.LoadBooking(theirs.Id))!.Status);
    }

    [Fact]
    public async Task Confirm_SkipsFutureBookings()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var future = await SeedBooking(venue.Id, daysAgo: -2, "confirmed", "20:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        await client.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { future.Id } });

        Assert.Equal("confirmed", (await _fx.LoadBooking(future.Id))!.Status);
    }

    [Fact]
    public async Task WriteStaff_CanConfirmAttendance()
    {
        // Marking who turned up is exactly the counter clerk's job.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedBooking(venue.Id, daysAgo: 1, "confirmed", "21:00");

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var res = await client.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { booking.Id } });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("completed", (await _fx.LoadBooking(booking.Id))!.Status);
    }

    [Fact]
    public async Task ReadStaff_CannotConfirmAttendance()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var booking = await SeedBooking(venue.Id, daysAgo: 1, "confirmed", "22:00");

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAReadId);
        await client.PostAsJsonAsync("/api/v1/bookings/attendance-confirm",
            new { bookingIds = new[] { booking.Id } });

        Assert.Equal("confirmed", (await _fx.LoadBooking(booking.Id))!.Status);
    }

    [Fact]
    public async Task IgnoringThePromptLeavesTheDataUsable()
    {
        // The whole design rests on this: an owner who never opens the prompt still gets
        // correct attendance counts, because attendance is DERIVED from
        // "past AND confirmed AND not no_show". The row simply stays at "confirmed".
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var ignored = await SeedBooking(venue.Id, daysAgo: 3, "confirmed", "23:00");

        var row = await _fx.LoadBooking(ignored.Id);
        Assert.Equal("confirmed", row!.Status);
        Assert.True(row.Date.Date < PlatformConstants.JordanToday());
    }
}
