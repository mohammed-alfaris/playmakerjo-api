using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// Where the line between a "read" clerk and a "write" clerk actually falls.
///
/// Two holes on either side of that line:
///
/// Cancel was gated on <c>CanAccessBooking</c> — the VIEW predicate, which admits read-only
/// staff. So a clerk explicitly set to read-only, whose whole point is watching the schedule
/// and touching nothing, could destroy any booking at their employer's venues. Every other
/// state change on the controller already used CanManageBooking; cancel was the exception.
///
/// And PATCH /users/{id}/role assigned the role column straight from the request body: no
/// validation of the value at all, and no employer link when promoting to venue_staff. The
/// token only carries owner_id and permissions when they are non-empty, so such an account
/// signed in perfectly and then reached nothing, with no error explaining why.
/// </summary>
[Collection("Api")]
public class ClerkWriteBoundaryTests
{
    private readonly DatabaseFixture _fx;

    public ClerkWriteBoundaryTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(11).ToString("yyyy-MM-dd");

    /// <summary>A confirmed counter booking on owner A's venue, made by the owner.</summary>
    private async Task<string> ABooking(string startTime)
    {
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = _fx.VenueAId,
            sport = "basketball",
            date = FutureDate,
            startTime,
            duration = 60,
            isManual = true,
            customerPhone = "0791234500",
            customerName = "Cancel Target",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
    }

    [Fact]
    public async Task AReadOnlyClerkCannotCancelABooking()
    {
        // The hole. Cancelling frees the slot and ends the booking — it is a write, and
        // "read" is supposed to mean watch the schedule and change nothing.
        var bookingId = await ABooking("09:00");
        var clerk = await _fx.CreateClientForUserAsync(_fx.StaffAReadId);

        var res = await clerk.PatchAsync($"/api/v1/bookings/{bookingId}/cancel", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var after = await _fx.LoadBooking(bookingId);
        Assert.NotEqual("cancelled", after!.Status);
    }

    [Fact]
    public async Task AWriteClerkStillCan()
    {
        // The other half. Narrowing the predicate must not take the counter clerk's actual
        // job away — a test that only pins the refusal above would pass if cancel were
        // broken for everyone.
        var bookingId = await ABooking("10:00");
        var clerk = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        var res = await clerk.PatchAsync($"/api/v1/bookings/{bookingId}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("cancelled", (await _fx.LoadBooking(bookingId))!.Status);
    }

    [Fact]
    public async Task AReadOnlyClerkCanStillSeeTheBooking()
    {
        // Read-only must remain genuinely readable; the fix narrowed cancel, not viewing.
        var bookingId = await ABooking("11:00");
        var clerk = await _fx.CreateClientForUserAsync(_fx.StaffAReadId);

        var res = await clerk.GetAsync($"/api/v1/bookings/{bookingId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task APlayerCanStillCancelTheirOwnBooking()
    {
        // The narrowed predicate must not catch the player it was never about.
        var player = await _fx.CreatePlayer();
        var playerClient = _fx.CreateClientFor(player.Id, "player");
        var created = await playerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = _fx.VenueAId,
            sport = "basketball",
            date = FutureDate,
            startTime = "12:00",
            duration = 60,
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var res = await playerClient.PatchAsync($"/api/v1/bookings/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task PromotingSomeoneToStaffWithNoEmployerIsRefused()
    {
        // Previously allowed, and it produced an account that logged in fine and then could
        // reach nothing at all — the worst kind of broken, because it looks like it worked.
        var player = await _fx.CreatePlayer();
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");

        var res = await admin.PatchAsJsonAsync(
            $"/api/v1/users/{player.Id}/role", new { role = "venue_staff" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var after = await _fx.LoadUser(player.Id);
        Assert.Equal("player", after!.Role);
    }

    [Fact]
    public async Task AnUnknownRoleStringIsRefused()
    {
        // The column took whatever arrived. Every authorisation check is an equality test
        // against a known literal, so a typo yields an account matching nothing, silently.
        var player = await _fx.CreatePlayer();
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");

        var res = await admin.PatchAsJsonAsync(
            $"/api/v1/users/{player.Id}/role", new { role = "venue_manager" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("player", (await _fx.LoadUser(player.Id))!.Role);
    }

    [Fact]
    public async Task DemotingAClerkDropsTheirEmployerLink()
    {
        // Otherwise a demoted clerk keeps a stale employer and permission, which would come
        // straight back the moment anyone set the role to venue_staff again.
        // A throwaway clerk, not the fixture's shared one — this test destroys what it uses.
        var clerk = await _fx.CreateStaff(_fx.OwnerAId, "write");
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");

        var res = await admin.PatchAsJsonAsync(
            $"/api/v1/users/{clerk.Id}/role", new { role = "player" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var after = await _fx.LoadUser(clerk.Id);
        Assert.Equal("player", after!.Role);
        Assert.Null(after.ManagedByOwnerId);
        Assert.Null(after.Permissions);
    }

    [Fact]
    public async Task AnExistingClerkCanStillBeReassignedToStaff()
    {
        // The employer-link guard must not lock out someone who already has one — it keys on
        // the stored link, not on the request, so a real clerk stays promotable.
        var clerk = await _fx.CreateStaff(_fx.OwnerAId, "read");
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");

        var res = await admin.PatchAsJsonAsync(
            $"/api/v1/users/{clerk.Id}/role", new { role = "venue_staff" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
