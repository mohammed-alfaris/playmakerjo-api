using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

[Collection("Api")]
public class UserCreationTests
{
    private readonly DatabaseFixture _fx;

    public UserCreationTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private static object UserBody(string role, string email) => new
    {
        name = "Created In Test",
        email,
        password = "Secret#12345",
        role
    };

    [Fact]
    public async Task Player_CreateUser_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/users",
            UserBody("venue_staff", "player-made-staff@test.local"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task OwnerWithVenues_CreateVenueStaff_Succeeds()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/users",
            UserBody("venue_staff", "staff-by-owner-a@test.local"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task VenuelessOwner_CreateVenueStaff_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.OwnerCId, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/users",
            UserBody("venue_staff", "staff-by-owner-c@test.local"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("must own at least one venue", body);
    }

    [Fact]
    public async Task Owner_CreateSuperAdmin_Returns403()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/users",
            UserBody("super_admin", "admin-by-owner-a@test.local"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_CreateVenueOwner_Succeeds()
    {
        var client = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var res = await client.PostAsJsonAsync("/api/v1/users",
            UserBody("venue_owner", "owner-by-admin@test.local"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
