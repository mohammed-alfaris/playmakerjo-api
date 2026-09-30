using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.App;

/// <summary>
/// The app has no cookie jar, so it used to lose its session every 15 minutes: refresh read
/// only the cookie. The app now asks for its refresh token in the body (X-Client: mobile),
/// sends it back to refresh, and gets a fresh one each time. Browsers are unchanged.
/// </summary>
[Collection("Api")]
public class MobileSessionTests
{
    private readonly DatabaseFixture _fx;

    public MobileSessionTests(DatabaseFixture fx) => _fx = fx;

    private static async Task<JsonElement> Data(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private async Task<HttpResponseMessage> Login(string email, bool mobile)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password = DatabaseFixture.TestPassword }),
        };
        if (mobile) req.Headers.Add("X-Client", "mobile");
        return await _fx.Factory.CreateClient().SendAsync(req);
    }

    private async Task<HttpResponseMessage> RefreshWithBody(string refreshToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new { refreshToken }),
        };
        req.Headers.Add("X-Client", "mobile");
        return await _fx.Factory.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task TheApp_GetsItsRefreshToken_ABrowserDoesNot()
    {
        var player = await _fx.CreatePlayer();

        var mobile = await Data(await Login(player.Email, mobile: true));
        var browser = await Data(await Login(player.Email, mobile: false));

        Assert.False(string.IsNullOrEmpty(mobile.GetProperty("refreshToken").GetString()));
        Assert.False(browser.TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task RefreshingFromTheBody_Works_AndHandsBackAFreshToken()
    {
        var player = await _fx.CreatePlayer();
        var first = (await Data(await Login(player.Email, mobile: true))).GetProperty("refreshToken").GetString()!;
        await Task.Delay(1100); // a new token in a new second, so it differs

        var res = await RefreshWithBody(first);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var data = await Data(res);
        var second = data.GetProperty("refreshToken").GetString()!;
        Assert.False(string.IsNullOrEmpty(data.GetProperty("accessToken").GetString()));
        Assert.NotEqual(first, second);

        Assert.Equal(HttpStatusCode.OK, (await RefreshWithBody(second)).StatusCode);
    }

    [Fact]
    public async Task ABrowserStillRefreshesFromItsCookie_AndGetsNoBodyToken()
    {
        var player = await _fx.CreatePlayer();
        var token = (await Data(await Login(player.Email, mobile: true))).GetProperty("refreshToken").GetString()!;

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        req.Headers.Add("Cookie", $"refresh_token={token}");
        var res = await _fx.Factory.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False((await Data(res)).TryGetProperty("refreshToken", out _));
    }

    [Fact]
    public async Task NoToken_OrAForgedOne_IsRefused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Factory.CreateClient().PostAsync("/api/v1/auth/refresh", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshWithBody("not-a-token")).StatusCode);
    }
}
