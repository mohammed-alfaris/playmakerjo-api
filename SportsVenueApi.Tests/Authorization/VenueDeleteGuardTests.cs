using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// DELETE /api/v1/venues/{venueId} had an ownership check and nothing else.
///
/// Every foreign key pointing at a venue is ON DELETE CASCADE — verified against the live
/// schema, not inferred: bookings, permanent_bookings, recurring_booking_groups, reviews and
/// favorites all cascade from venues, and payments cascades from bookings. So one authorised
/// click destroyed the venue, every booking ever taken there, and the append-only payment
/// ledger for all of them. There is no soft delete and no undo, and at the time this was
/// written the server had no working automated backups either.
///
/// The pitch-removal guard in Update counts only FUTURE bookings, and is right to: retiring a
/// pitch strands history, and history is allowed to keep a dead pitch id. That reasoning does
/// not carry over. Here a PAST booking is the one that matters, because it is the row the
/// money hangs off.
/// </summary>
[Collection("Api")]
public class VenueDeleteGuardTests
{
    private readonly DatabaseFixture _fx;

    public VenueDeleteGuardTests(DatabaseFixture fx) => _fx = fx;

    private HttpClient Owner => _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

    private async Task<string> BookAt(string venueId, string date, string startTime)
    {
        var res = await Owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId,
            sport = "basketball",
            date,
            startTime,
            duration = 60,
            isManual = true,
            customerPhone = "0791234599",
            customerName = "History",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
    }

    [Fact]
    public async Task AnEmptyVenueCanStillBeDeleted()
    {
        // The guard must not turn into "nothing is ever deletable" — a venue created by
        // mistake, before anyone booked it, is exactly what Delete is for.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);

        var res = await Owner.DeleteAsync($"/api/v1/venues/{venue.Id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Null(await _fx.LoadVenue(venue.Id));
    }

    [Fact]
    public async Task AVenueWithAFutureBookingIsRefused()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        await BookAt(venue.Id, PlatformConstants.JordanToday().AddDays(6).ToString("yyyy-MM-dd"), "09:00");

        var res = await Owner.DeleteAsync($"/api/v1/venues/{venue.Id}");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.NotNull(await _fx.LoadVenue(venue.Id));
    }

    [Fact]
    public async Task AVenueWithOnlyPastBookingsIsAlsoRefused()
    {
        // The case the pitch guard's date filter would have let through, and the one that
        // actually matters: a past booking is where the money is recorded.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var bookingId = await BookAt(venue.Id, PlatformConstants.JordanToday().AddDays(4).ToString("yyyy-MM-dd"), "10:00");

        // Move it into the past directly — the API will not create a booking in the past.
        await _fx.BackdateBooking(bookingId, PlatformConstants.JordanToday().AddDays(-30));

        var res = await Owner.DeleteAsync($"/api/v1/venues/{venue.Id}");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.NotNull(await _fx.LoadVenue(venue.Id));
    }

    [Fact]
    public async Task TheRefusalLeavesTheBookingAndItsPaymentIntact()
    {
        // The whole point. Asserting only on the 409 would pass even if the rows had already
        // been cascaded away before the check ran.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var bookingId = await BookAt(venue.Id, PlatformConstants.JordanToday().AddDays(5).ToString("yyyy-MM-dd"), "11:00");

        var paid = await Owner.PatchAsync($"/api/v1/bookings/{bookingId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Single(await _fx.LoadPayments(bookingId));

        var res = await Owner.DeleteAsync($"/api/v1/venues/{venue.Id}");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        Assert.NotNull(await _fx.LoadVenue(venue.Id));
        Assert.NotNull(await _fx.LoadBooking(bookingId));
        Assert.Single(await _fx.LoadPayments(bookingId));
    }

    [Fact]
    public async Task ACancelledBookingDoesNotBlockDeletion()
    {
        // A cancelled booking holds no slot and carries no collectable money, so it should
        // not permanently freeze a venue that never really traded.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var bookingId = await BookAt(venue.Id, PlatformConstants.JordanToday().AddDays(7).ToString("yyyy-MM-dd"), "12:00");
        Assert.Equal(HttpStatusCode.OK,
            (await Owner.PatchAsync($"/api/v1/bookings/{bookingId}/cancel", null)).StatusCode);

        var res = await Owner.DeleteAsync($"/api/v1/venues/{venue.Id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AStandingReservationAloneBlocksDeletion()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var created = await Owner.PostAsJsonAsync($"/api/v1/venues/{venue.Id}/permanent-bookings", new
        {
            dayOfWeek = 2, startTime = "20:00", duration = 60,
            label = "Weekly group", customerPhone = "0791234598", customerName = "Weekly",
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        var res = await Owner.DeleteAsync($"/api/v1/venues/{venue.Id}");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.NotNull(await _fx.LoadVenue(venue.Id));
    }

    [Fact]
    public async Task AnotherOwnerStillCannotDeleteIt()
    {
        // The guard is additional to the ownership check, not a replacement for it.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var intruder = _fx.CreateClientFor(_fx.OwnerBId, "venue_owner");

        var res = await intruder.DeleteAsync($"/api/v1/venues/{venue.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotNull(await _fx.LoadVenue(venue.Id));
    }
}
