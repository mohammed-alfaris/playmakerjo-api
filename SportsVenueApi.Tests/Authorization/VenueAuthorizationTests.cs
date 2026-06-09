using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Data;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

[Collection("Api")]
public class VenueAuthorizationTests
{
    private readonly DatabaseFixture _fx;

    public VenueAuthorizationTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private async Task<Venue?> LoadVenue(string id)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id);
    }

    private async Task<string> InsertVenue(string ownerId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var venue = new Venue
        {
            Name = "Throwaway Venue", OwnerId = ownerId, Sports = ["basketball"],
            City = "Amman", Address = "Throwaway Street", PricePerHour = 10, Status = "active"
        };
        db.Venues.Add(venue);
        await db.SaveChangesAsync();
        return venue.Id;
    }

    // ---- player: no venue management at all ----

    [Fact]
    public async Task Player_PatchVenue_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PatchAsJsonAsync($"/api/v1/venues/{_fx.VenueAId}",
            new { cliqAlias = "attacker@cliq" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var venue = await LoadVenue(_fx.VenueAId);
        Assert.Equal("venue-a@cliq", venue!.CliqAlias);
    }

    [Fact]
    public async Task Player_DeleteVenue_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.DeleteAsync($"/api/v1/venues/{_fx.VenueAId}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotNull(await LoadVenue(_fx.VenueAId));
    }

    [Fact]
    public async Task Player_CreateVenue_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/venues", new
        {
            name = "Player Venue",
            city = "Amman",
            address = "Nowhere 1",
            pricePerHour = 15,
            sports = new[] { "basketball" }
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Player_GetVenueStats_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.GetAsync($"/api/v1/venues/{_fx.VenueAId}/stats");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- venue_owner: scoped to own venues ----

    [Fact]
    public async Task OwnerA_PatchVenueB_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsJsonAsync($"/api/v1/venues/{_fx.VenueBId}",
            new { description = "owned by someone else" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task OwnerA_DeleteVenueB_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.DeleteAsync($"/api/v1/venues/{_fx.VenueBId}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotNull(await LoadVenue(_fx.VenueBId));
    }

    [Fact]
    public async Task OwnerA_GetVenueBStats_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/venues/{_fx.VenueBId}/stats");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task OwnerA_PatchOwnVenue_Succeeds()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsJsonAsync($"/api/v1/venues/{_fx.VenueAId}",
            new { description = "Updated by owner A" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var venue = await LoadVenue(_fx.VenueAId);
        Assert.Equal("Updated by owner A", venue!.Description);
    }

    [Fact]
    public async Task OwnerA_ReassignOwnership_Returns403_AndOwnerUnchanged()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsJsonAsync($"/api/v1/venues/{_fx.VenueAId}",
            new { owner_id = _fx.OwnerBId });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var venue = await LoadVenue(_fx.VenueAId);
        Assert.Equal(_fx.OwnerAId, venue!.OwnerId);
    }

    [Fact]
    public async Task OwnerA_SelfEchoOwnerId_IsNoOp()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsJsonAsync($"/api/v1/venues/{_fx.VenueAId}",
            new { owner_id = _fx.OwnerAId });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var venue = await LoadVenue(_fx.VenueAId);
        Assert.Equal(_fx.OwnerAId, venue!.OwnerId);
    }

    [Fact]
    public async Task OwnerA_GetOwnVenueStats_Returns200()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/venues/{_fx.VenueAId}/stats");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ---- super_admin: full access ----

    [Fact]
    public async Task Admin_ReassignOwnership_Succeeds()
    {
        var venueId = await InsertVenue(_fx.OwnerAId);

        var client = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var res = await client.PatchAsJsonAsync($"/api/v1/venues/{venueId}",
            new { owner_id = _fx.OwnerBId });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var venue = await LoadVenue(venueId);
        Assert.Equal(_fx.OwnerBId, venue!.OwnerId);
    }

    [Fact]
    public async Task Admin_DeleteVenue_Succeeds()
    {
        var venueId = await InsertVenue(_fx.OwnerAId);

        var client = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var res = await client.DeleteAsync($"/api/v1/venues/{venueId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Null(await LoadVenue(venueId));
    }
}
