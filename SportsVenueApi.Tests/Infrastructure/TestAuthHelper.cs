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
    public static HttpClient CreateClientFor(this DatabaseFixture fx, string userId, string role)
    {
        var jwt = fx.Factory.Services.GetRequiredService<JwtService>();
        var client = fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt.CreateAccessToken(userId, role));
        return client;
    }
}
