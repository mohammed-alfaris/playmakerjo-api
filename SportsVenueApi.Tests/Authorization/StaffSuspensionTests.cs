using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// An owner can suspend their own staff — and nobody else's.
///
/// PATCH /users/{id}/status was super_admin only while the dashboard showed owners a
/// Suspend button on My Team. The button 403'd behind a generic error toast, so an owner
/// could believe they had cut off a clerk who in fact still had a working login. Suspending
/// someone who no longer works for you is the most basic thing an employer needs; having to
/// phone the vendor for it is how a departed clerk keeps access for a week.
///
/// The interesting half of this file is the second test: opening the route to owners must
/// not let one owner reach another owner's people.
/// </summary>
[Collection("Api")]
public class StaffSuspensionTests
{
    private readonly DatabaseFixture _fx;

    public StaffSuspensionTests(DatabaseFixture fx) => _fx = fx;

    private Task<HttpResponseMessage> SetStatus(HttpClient client, string userId, string status) =>
        client.PatchAsJsonAsync($"/api/v1/users/{userId}/status", new { status });

    [Fact]
    public async Task AnOwnerCanSuspendAndRestoreTheirOwnClerk()
    {
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var suspend = await SetStatus(owner, _fx.StaffAReadId, "banned");
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        var restore = await SetStatus(owner, _fx.StaffAReadId, "active");
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
    }

    [Fact]
    public async Task AnOwnerCannotTouchAnotherOwnersStaff()
    {
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await SetStatus(owner, _fx.StaffBWriteId, "banned");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AnOwnerCannotBanAnotherOwner()
    {
        // The route now accepts non-admins, so it has to refuse everything that is not
        // "my own staff" — including peers, players and admins.
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(owner, _fx.OwnerBId, "banned")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(owner, _fx.PlayerId, "banned")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(owner, _fx.AdminId, "banned")).StatusCode);
    }

    [Fact]
    public async Task StaffCannotSuspendAnyone()
    {
        // Including themselves, and including their colleagues — My Team is owner-only.
        var staff = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(staff, _fx.StaffAReadId, "banned")).StatusCode);
    }

    [Fact]
    public async Task AnUnknownStatusIsRefused()
    {
        // The field was written straight to the column with no validation, so any string at
        // all became a user's status — and every status check in the system compares against
        // "banned" or "active" exactly.
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");

        var res = await SetStatus(admin, _fx.StaffAReadId, "sleeping");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AdminsKeepTheirReach()
    {
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");

        Assert.Equal(HttpStatusCode.OK, (await SetStatus(admin, _fx.StaffBWriteId, "banned")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(admin, _fx.StaffBWriteId, "active")).StatusCode);
    }
}
