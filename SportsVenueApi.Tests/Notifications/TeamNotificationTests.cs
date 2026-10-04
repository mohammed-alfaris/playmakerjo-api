using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Services;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Notifications;

/// <summary>
/// The venue's whole team hears about what concerns them — not only the owner: staff whose role
/// covers it and who work at that venue. Whoever did the thing is not told about it.
/// </summary>
[Collection("Api")]
public class TeamNotificationTests
{
    private readonly DatabaseFixture _fx;

    public TeamNotificationTests(DatabaseFixture fx) => _fx = fx;

    private static string Date(int days) => PlatformConstants.JordanToday().AddDays(days).ToString("yyyy-MM-dd");

    private async Task<List<(string Type, string? Ref)>> Inbox(string userId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Notifications.Where(n => n.UserId == userId)
                .Select(n => new { n.Type, n.ReferenceId }).ToListAsync())
            .Select(n => (n.Type, n.ReferenceId)).ToList();
    }

    private async Task<User> StaffWithRole(string ownerId, string[] permissions, string[]? onlyVenues = null)
    {
        var role = await _fx.Insert(new StaffRole { OwnerId = ownerId, Name = "Role " + Guid.NewGuid().ToString("N")[..6], Permissions = permissions.ToList() });
        var staff = await _fx.CreateStaff(ownerId);
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.Users.FirstAsync(u => u.Id == staff.Id);
        row.StaffRoleId = role.Id;
        if (onlyVenues != null)
        {
            row.StaffAllVenues = false;
            row.StaffVenueIds = onlyVenues.ToList();
        }
        await db.SaveChangesAsync();
        return row;
    }

    private async Task<string> AppBooking(string venueId, string playerId, int days = 5, string time = "18:00")
    {
        var res = await _fx.CreateClientFor(playerId, "player").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId, sport = "basketball", date = Date(days), startTime = time, duration = 60, paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
    }

    [Fact]
    public async Task ANewBooking_ReachesStaffWhoSeeBookingsAtThatVenue_AndNoOtherStaff()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var otherVenue = await _fx.CreateBasketballVenue(owner.Id);
        var desk = await StaffWithRole(owner.Id, [StaffPermissions.BookingsView]);
        var accountant = await StaffWithRole(owner.Id, [StaffPermissions.ReportsView]);
        var elsewhere = await StaffWithRole(owner.Id, [StaffPermissions.BookingsView], onlyVenues: [otherVenue.Id]);
        var player = await _fx.CreatePlayer();

        var id = await AppBooking(venue.Id, player.Id);

        Assert.Contains(("new_booking", id), await Inbox(owner.Id));
        Assert.Contains(("new_booking", id), await Inbox(desk.Id));
        Assert.DoesNotContain(await Inbox(accountant.Id), n => n.Type == "new_booking");
        Assert.DoesNotContain(await Inbox(elsewhere.Id), n => n.Type == "new_booking");
    }

    [Fact]
    public async Task APaymentProof_ReachesStaffWhoRecordPayments_Only()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var cashier = await StaffWithRole(owner.Id, [StaffPermissions.BookingsView, StaffPermissions.PaymentsRecord]);
        var viewer = await StaffWithRole(owner.Id, [StaffPermissions.BookingsView]);
        var player = await _fx.CreatePlayer();
        var id = await AppBooking(venue.Id, player.Id);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var booking = await db.Bookings.Include(b => b.Venue).Include(b => b.Player).FirstAsync(b => b.Id == id);
            await scope.ServiceProvider.GetRequiredService<NotificationService>().NotifyProofReceived(booking);
        }

        Assert.Contains(("proof_received", id), await Inbox(owner.Id));
        Assert.Contains(("proof_received", id), await Inbox(cashier.Id));
        Assert.DoesNotContain(await Inbox(viewer.Id), n => n.Type == "proof_received");
    }

    [Fact]
    public async Task ACancellationByStaff_TellsTheOwnerAndThePlayer_NotTheClerkWhoDidIt()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var clerk = await StaffWithRole(owner.Id, [StaffPermissions.BookingsView, StaffPermissions.BookingsManage]);
        var player = await _fx.CreatePlayer();
        var id = await AppBooking(venue.Id, player.Id);

        var client = await _fx.CreateClientForUserAsync(clerk.Id);
        var res = await client.PatchAsJsonAsync($"/api/v1/bookings/{id}/cancel", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Contains(("booking_cancelled", id), await Inbox(owner.Id));
        Assert.Contains(("booking_cancelled", id), await Inbox(player.Id));
        Assert.DoesNotContain(await Inbox(clerk.Id), n => n.Type == "booking_cancelled");
    }

    [Fact]
    public async Task ConfirmingAnAppBookingByHand_TellsThePlayer()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();
        var id = await AppBooking(venue.Id, player.Id);

        var res = await _fx.CreateClientFor(owner.Id, "venue_owner").PatchAsync($"/api/v1/bookings/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Contains(("booking_confirmed", id), await Inbox(player.Id));
    }

    [Fact]
    public async Task AWeeklySeriesCancelledByThePlayer_IsOneNoticeToTheVenue()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();
        var playerClient = _fx.CreateClientFor(player.Id, "player");

        var res = await playerClient.PostAsJsonAsync("/api/v1/bookings/recurring", new
        {
            venueId = venue.Id, sport = "basketball", startDate = Date(7), endDate = Date(28), startTime = "10:00",
            duration = 60, recurrenceType = "weekly", paymentMethod = "cliq", conflictPolicy = "skip",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        string groupId;
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            groupId = (await db.RecurringBookingGroups.FirstAsync(g => g.VenueId == venue.Id)).Id;
        }

        var cancel = await playerClient.PatchAsync($"/api/v1/bookings/recurring/{groupId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        Assert.Single(await Inbox(owner.Id), n => n.Type == "series_cancelled");
        Assert.DoesNotContain(await Inbox(player.Id), n => n.Type == "series_cancelled");
    }

    [Fact]
    public async Task AReview_ReachesTheOwner()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();
        await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = player.Id, Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(-2), StartTime = "18:00", Duration = 60,
            Amount = 20, TotalAmount = 20, Status = "completed",
        });

        var res = await _fx.CreateClientFor(player.Id, "player").PostAsJsonAsync("/api/v1/reviews", new
        {
            venueId = venue.Id, rating = 4, comment = "Good pitch",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Contains(("new_review", venue.Id), await Inbox(owner.Id));
    }

    [Fact]
    public async Task APhoneSignedInByANewAccount_StopsGettingTheOldAccountsNotifications()
    {
        var first = await _fx.CreatePlayer();
        var second = await _fx.CreatePlayer();
        var token = "tok-" + Guid.NewGuid().ToString("N");

        var a = await _fx.CreateClientFor(first.Id, "player").PostAsJsonAsync("/api/v1/notifications/device-token", new { token, platform = "android" });
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        var b = await _fx.CreateClientFor(second.Id, "player").PostAsJsonAsync("/api/v1/notifications/device-token", new { token, platform = "android" });
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.DeviceTokens.Where(d => d.Token == token).ToListAsync();
        Assert.False(rows.Single(r => r.UserId == first.Id).IsActive);
        Assert.True(rows.Single(r => r.UserId == second.Id).IsActive);
    }
}
