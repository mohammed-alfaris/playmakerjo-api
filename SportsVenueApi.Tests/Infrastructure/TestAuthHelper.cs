using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Services;

namespace SportsVenueApi.Tests.Infrastructure;

/// <summary>
/// Mints access tokens directly via JwtService — never call /auth/login in tests,
/// the global 5/min auth rate limit would make them flaky.
/// </summary>
public static class TestAuthHelper
{
    public static HttpClient CreateClientFor(
        this DatabaseFixture fx, string userId, string role,
        string? managedByOwnerId = null, string? permissions = null)
    {
        var jwt = fx.Factory.Services.GetRequiredService<JwtService>();
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer",
                jwt.CreateAccessToken(userId, role, managedByOwnerId, permissions));
        return client;
    }

    /// <summary>
    /// Mints a token from the user's ACTUAL database row, so the staff claims match what a
    /// real login would produce. Prefer this for staff — passing claims by hand makes it
    /// easy to write a test that passes against a token no login could ever issue.
    /// </summary>
    public static async Task<HttpClient> CreateClientForUserAsync(this DatabaseFixture fx, string userId)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SportsVenueApi.Data.AppDbContext>();
        var user = await db.Users.FindAsync(userId)
            ?? throw new InvalidOperationException($"No seeded user '{userId}'.");

        return fx.CreateClientFor(user.Id, user.Role, user.ManagedByOwnerId, user.Permissions);
    }
}
