using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Auth;
using SportsVenueApi.Models;
using SportsVenueApi.Services;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Auth;

/// <summary>
/// Integration tests for the auth flows (login, register, refresh, logout).
/// Cookies are handled manually (HandleCookies = false) so Set-Cookie headers
/// stay observable and the refresh cookie is controlled explicitly. Users are
/// inserted per-test with GUID emails so the shared fixture seed is never mutated.
/// </summary>
[Collection("Api")]
public class AuthFlowTests
{
    private readonly DatabaseFixture _fx;

    public AuthFlowTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private HttpClient AnonClient() =>
        _fx.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    private static string UniqueEmail() => $"u-{Guid.NewGuid():N}@test.local";

    private async Task<User> InsertUser(string password, string status = "active", string role = "player")
    {
        return await _fx.Insert(new User
        {
            Name = "Auth Test User",
            Email = UniqueEmail(),
            Phone = "+962790009999",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            Status = status
        });
    }

    // ---- Login -------------------------------------------------------------

    [Fact]
    public async Task Login_WithValidCredentials_Returns200WithTokenAndCookie()
    {
        var password = "Test#12345";
        var user = await InsertUser(password);
        var client = AnonClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = user.Email, password });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<LoginData>>();
        Assert.True(body!.Success);
        Assert.False(string.IsNullOrEmpty(body.Data!.AccessToken));
        Assert.Equal(user.Email, body.Data.User.Email);
        Assert.Equal("player", body.Data.User.Role);

        Assert.True(res.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies!, c => c.Contains("refresh_token"));
    }

    [Fact]
    public async Task Login_BannedUser_Returns403()
    {
        var password = "Test#12345";
        var user = await InsertUser(password, status: "banned");
        var client = AnonClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = user.Email, password });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ---- Register ----------------------------------------------------------

    [Fact]
    public async Task Register_WithValidPayload_Returns200AndDefaultsToPlayer()
    {
        var client = AnonClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            name = "New Player",
            email = UniqueEmail(),
            phone = "+962790001234",
            password = "Sup3rSecret"
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<LoginData>>();
        Assert.True(body!.Success);
        Assert.False(string.IsNullOrEmpty(body.Data!.AccessToken));
        Assert.Equal("player", body.Data.User.Role);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        var existing = await InsertUser("Test#12345");
        var client = AnonClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            name = "Dupe",
            email = existing.Email,
            password = "Sup3rSecret"
        });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Register_ShortPassword_Returns400()
    {
        var client = AnonClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            name = "Weak",
            email = UniqueEmail(),
            password = "short"
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ---- Refresh -----------------------------------------------------------

    [Fact]
    public async Task Refresh_WithValidCookie_Returns200WithNewAccessToken()
    {
        var user = await InsertUser("Test#12345");
        var refreshToken = MintRefreshToken(user);
        var client = AnonClient();
        client.DefaultRequestHeaders.Add("Cookie", $"refresh_token={refreshToken}");

        var res = await client.PostAsync("/api/v1/auth/refresh", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<TokenData>>();
        Assert.False(string.IsNullOrEmpty(body!.Data!.AccessToken));
    }

    [Fact]
    public async Task Refresh_WithoutCookie_Returns401()
    {
        var client = AnonClient();

        var res = await client.PostAsync("/api/v1/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_BannedUser_Returns401()
    {
        var user = await InsertUser("Test#12345", status: "banned");
        var refreshToken = MintRefreshToken(user);
        var client = AnonClient();
        client.DefaultRequestHeaders.Add("Cookie", $"refresh_token={refreshToken}");

        var res = await client.PostAsync("/api/v1/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithAccessTokenInCookie_Returns401()
    {
        // An access token must not be accepted where a refresh token is expected.
        var user = await InsertUser("Test#12345");
        var jwt = _fx.Factory.Services.GetRequiredService<JwtService>();
        var accessToken = jwt.CreateAccessToken(user.Id, user.Role);
        var client = AnonClient();
        client.DefaultRequestHeaders.Add("Cookie", $"refresh_token={accessToken}");

        var res = await client.PostAsync("/api/v1/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ---- Logout ------------------------------------------------------------

    [Fact]
    public async Task Logout_Returns200AndClearsCookie()
    {
        var client = AnonClient();

        var res = await client.PostAsync("/api/v1/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies!, c => c.Contains("refresh_token"));
    }

    private string MintRefreshToken(User user)
    {
        var jwt = _fx.Factory.Services.GetRequiredService<JwtService>();
        return jwt.CreateRefreshToken(user.Id, user.Role);
    }
}
