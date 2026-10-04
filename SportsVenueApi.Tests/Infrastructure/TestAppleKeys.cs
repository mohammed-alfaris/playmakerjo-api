using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using SportsVenueApi.Services;

namespace SportsVenueApi.Tests.Infrastructure;

/// <summary>
/// A stand-in for Apple's key server: one RSA key the API is told to trust in tests, and a way
/// to mint identity tokens signed with it — or with a key it does not trust.
/// </summary>
public static class TestAppleKeys
{
    private static readonly RsaSecurityKey Trusted = new(RSA.Create(2048)) { KeyId = "test-apple-key" };

    public static IAppleKeySource Source { get; } = new FixedSource(Trusted);

    public static string Token(
        string sub, string? email = null, bool emailVerified = true, string audience = "com.playmakerjo.app",
        string issuer = "https://appleid.apple.com", DateTime? expires = null, bool untrustedKey = false)
    {
        var key = untrustedKey ? new RsaSecurityKey(RSA.Create(2048)) { KeyId = "test-apple-key" } : Trusted;
        var claims = new List<Claim> { new("sub", sub) };
        if (email != null)
        {
            claims.Add(new Claim("email", email));
            claims.Add(new Claim("email_verified", emailVerified ? "true" : "false"));
        }
        var until = expires ?? DateTime.UtcNow.AddMinutes(10);
        var token = new JwtSecurityToken(
            issuer, audience, claims,
            // Valid from an hour before it ends, so an already-expired token can be made too.
            notBefore: until.AddHours(-1),
            expires: until,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class FixedSource(SecurityKey key) : IAppleKeySource
    {
        public Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<SecurityKey>>([key]);
    }
}
