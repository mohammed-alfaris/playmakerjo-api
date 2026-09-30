using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Notifications;

/// <summary>
/// The owner's inbox: a booking made in the app reaches the owner; one keyed in at the counter
/// does not (the person who made it is standing at the desk); a weekly series is one notice.
/// </summary>
[Collection("Api")]
public class NewBookingNotificationTests
{
    private readonly DatabaseFixture _fx;

    public NewBookingNotificationTests(DatabaseFixture fx) => _fx = fx;

    private static string Date(int days) => PlatformConstants.JordanToday().AddDays(days).ToString("yyyy-MM-dd");

    private async Task<List<(string Type, string? Ref)>> OwnerInbox(string ownerId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Notifications.Where(n => n.UserId == ownerId)
                .Select(n => new { n.Type, n.ReferenceId }).ToListAsync())
            .Select(n => (n.Type, n.ReferenceId)).ToList();
    }

    [Fact]
    public async Task AnAppBooking_ReachesTheOwner()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();

        var res = await _fx.CreateClientFor(player.Id, "player").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Date(5), startTime = "18:00", duration = 60, paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        Assert.Contains(("new_booking", id), await OwnerInbox(owner.Id));
    }

    [Fact]
    public async Task ACounterBooking_DoesNotNotify()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);

        var res = await _fx.CreateClientFor(owner.Id, "venue_owner").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Date(5), startTime = "18:00", duration = 60,
            paymentMethod = "cliq", isManual = true, customerPhone = "0791234567", customerName = "Walk In",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.DoesNotContain(await OwnerInbox(owner.Id), n => n.Type == "new_booking");
    }

    [Fact]
    public async Task AWeeklySeries_IsOneNotice()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();

        var res = await _fx.CreateClientFor(player.Id, "player").PostAsJsonAsync("/api/v1/bookings/recurring", new
        {
            venueId = venue.Id, sport = "basketball", startDate = Date(7), endDate = Date(28), startTime = "10:00",
            duration = 60, recurrenceType = "weekly", paymentMethod = "cliq", conflictPolicy = "skip",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var inbox = await OwnerInbox(owner.Id);
        Assert.Single(inbox, n => n.Type == "new_series");
        Assert.DoesNotContain(inbox, n => n.Type == "new_booking");
    }
}
