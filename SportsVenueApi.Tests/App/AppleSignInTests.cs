using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.App;

/// <summary>
/// Sign in with Apple: required by Apple because the app offers Google sign-in. The token must
/// be Apple's (signature, issuer), for this app (audience) and current; the person is found by
/// Apple's own id first, then by a verified email, else created.
/// </summary>
[Collection("Api")]
public class AppleSignInTests
{
    private readonly DatabaseFixture _fx;

    public AppleSignInTests(DatabaseFixture fx) => _fx = fx;

    private Task<HttpResponseMessage> SignIn(string token, string? name = null) =>
        _fx.Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/apple", new { identityToken = token, name });

    private static async Task<JsonElement> User(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("user");

    [Fact]
    public async Task AFirstSignIn_CreatesAPlayer_NamedFromWhatAppleGaveTheApp()
    {
        var sub = "apple-" + Guid.NewGuid().ToString("N");
        var email = $"{sub}@privaterelay.appleid.com";

        var res = await SignIn(TestAppleKeys.Token(sub, email), name: "Noor Hassan");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var user = await User(res);
        Assert.Equal(("Noor Hassan", "player", email), (user.GetProperty("name").GetString(), user.GetProperty("role").GetString(), user.GetProperty("email").GetString()));
        Assert.Equal(JsonValueKind.Null, user.GetProperty("phone").ValueKind); // collected on complete-profile
    }

    [Fact]
    public async Task TheSamePerson_IsFoundByAppleId_EvenIfTheirEmailChanged()
    {
        var sub = "apple-" + Guid.NewGuid().ToString("N");
        var first = await User(await SignIn(TestAppleKeys.Token(sub, $"{sub}@one.example")));
        var again = await User(await SignIn(TestAppleKeys.Token(sub, $"{sub}@two.example")));

        Assert.Equal(first.GetProperty("id").GetString(), again.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AnExistingAccount_WithTheSameVerifiedEmail_IsLinked_NotDuplicated()
    {
        var player = await _fx.CreatePlayer();

        var res = await SignIn(TestAppleKeys.Token("apple-" + Guid.NewGuid().ToString("N"), player.Email));

        Assert.Equal(player.Id, (await User(res)).GetProperty("id").GetString());
    }

    [Fact]
    public async Task AnUnverifiedEmail_DoesNotTakeOverAnAccount()
    {
        var player = await _fx.CreatePlayer();

        var res = await SignIn(TestAppleKeys.Token("apple-" + Guid.NewGuid().ToString("N"), player.Email, emailVerified: false));

        // A new account cannot be made on an address someone else holds either.
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("key")]
    [InlineData("expired")]
    public async Task ATokenNotFromAppleForThisApp_IsRefused(string wrong)
    {
        var sub = "apple-" + Guid.NewGuid().ToString("N");
        var token = wrong switch
        {
            "audience" => TestAppleKeys.Token(sub, $"{sub}@x.example", audience: "com.someone.else"),
            "issuer" => TestAppleKeys.Token(sub, $"{sub}@x.example", issuer: "https://accounts.google.com"),
            "key" => TestAppleKeys.Token(sub, $"{sub}@x.example", untrustedKey: true),
            _ => TestAppleKeys.Token(sub, $"{sub}@x.example", expires: DateTime.UtcNow.AddMinutes(-10)),
        };

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(token)).StatusCode);
    }

    [Fact]
    public async Task ANewPersonWithNoEmail_CannotBeCreated()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await SignIn(TestAppleKeys.Token("apple-" + Guid.NewGuid().ToString("N")))).StatusCode);
    }
}
