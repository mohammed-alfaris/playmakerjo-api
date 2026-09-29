using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Data;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// Access is read from the database on every request, not from the token.
///
/// A token is a snapshot of login time. While access came from its claims, suspending a clerk
/// took up to one token lifetime to bite, and a banned owner's staff were never cut off at all —
/// login and refresh only checked the clerk's own status, and nothing looked at the employer's.
/// Each test here holds one token across a change made underneath it.
/// </summary>
[Collection("Api")]
public class AccessContextTests
{
    private readonly DatabaseFixture _fx;

    public AccessContextTests(DatabaseFixture fx) => _fx = fx;

    private async Task SetStatus(string userId, string status)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId);
        user.Status = status;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SuspendingAClerk_TakesEffectOnTheirNextRequest()
    {
        var owner = await _fx.CreateOwner();
        await _fx.CreateBasketballVenue(owner.Id);
        var clerk = await _fx.CreateStaff(owner.Id, "write");
        var client = await _fx.CreateClientForUserAsync(clerk.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/bookings")).StatusCode);

        await SetStatus(clerk.Id, "banned");

        // Same token, minutes from expiry or not: the row says suspended, so no back office.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/bookings")).StatusCode);
    }

    [Fact]
    public async Task ABannedOwnersStaff_LoseAccessToo()
    {
        var owner = await _fx.CreateOwner();
        await _fx.CreateBasketballVenue(owner.Id);
        var clerk = await _fx.CreateStaff(owner.Id, "write");
        var client = await _fx.CreateClientForUserAsync(clerk.Id);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/bookings")).StatusCode);

        await SetStatus(owner.Id, "banned");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/bookings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/customers")).StatusCode);
    }

    [Fact]
    public async Task AClerkMovedToAnotherEmployer_SeesOnlyTheNewOne()
    {
        var oldBoss = await _fx.CreateOwner();
        var newBoss = await _fx.CreateOwner();
        var oldVenue = await _fx.CreateBasketballVenue(oldBoss.Id);
        var clerk = await _fx.CreateStaff(oldBoss.Id, "write");
        var client = await _fx.CreateClientForUserAsync(clerk.Id);  // token still says oldBoss

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Users.FirstAsync(u => u.Id == clerk.Id);
            row.ManagedByOwnerId = newBoss.Id;
            await db.SaveChangesAsync();
        }

        var res = await client.GetAsync($"/api/v1/venues/{oldVenue.Id}/permanent-bookings");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
