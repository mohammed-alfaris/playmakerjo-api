using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using SportsVenueApi.Services;

namespace SportsVenueApi.Tests.Services;

/// <summary>
/// Pure unit tests for JwtService — no database or WebApplicationFactory.
/// Each test builds an in-memory IConfiguration with a known secret so token
/// round-trips are deterministic.
/// </summary>
public class JwtServiceTests
{
    // HS256 requires a key of at least 256 bits (32 bytes).
    private const string Secret = "test-secret-key-that-is-long-enough-1234567890";

    private static JwtService Build(string secret = Secret, string accessMinutes = "15")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = secret,
                ["Jwt:AccessTokenExpireMinutes"] = accessMinutes,
                ["Jwt:RefreshTokenExpireDays"] = "7"
            })
            .Build();
        return new JwtService(config);
    }

    [Fact]
    public void CreateAccessToken_ThenValidate_ReturnsPrincipalWithClaims()
    {
        var jwt = Build();

        var token = jwt.CreateAccessToken("user-123", "super_admin");
        var principal = jwt.ValidateToken(token, "access");

        Assert.NotNull(principal);
        // Sub maps onto NameIdentifier under the default inbound claim mapping.
        var sub = principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? principal.FindFirst("sub")?.Value;
        Assert.Equal("user-123", sub);
        // The handler's default inbound claim mapping remaps "role" onto ClaimTypes.Role
        // (same mapping that turns "sub" into NameIdentifier).
        var role = principal.FindFirst(ClaimTypes.Role)?.Value ?? principal.FindFirst("role")?.Value;
        Assert.Equal("super_admin", role);
        Assert.Equal("access", principal.FindFirst("type")?.Value);
    }

    [Fact]
    public void CreateRefreshToken_ThenValidate_Succeeds()
    {
        var jwt = Build();

        var token = jwt.CreateRefreshToken("user-456", "player");
        var principal = jwt.ValidateToken(token, "refresh");

        Assert.NotNull(principal);
        Assert.Equal("refresh", principal!.FindFirst("type")?.Value);
    }

    [Fact]
    public void AccessToken_ValidatedAsRefresh_ReturnsNull()
    {
        var jwt = Build();

        var token = jwt.CreateAccessToken("user-123", "venue_owner");

        Assert.Null(jwt.ValidateToken(token, "refresh"));
    }

    [Fact]
    public void RefreshToken_ValidatedAsAccess_ReturnsNull()
    {
        var jwt = Build();

        var token = jwt.CreateRefreshToken("user-123", "venue_owner");

        Assert.Null(jwt.ValidateToken(token, "access"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("aaa.bbb.ccc")]
    public void ValidateToken_GarbageInput_ReturnsNull(string garbage)
    {
        var jwt = Build();

        Assert.Null(jwt.ValidateToken(garbage, "access"));
    }

    [Fact]
    public void Token_TamperedPayload_ReturnsNull()
    {
        var jwt = Build();
        var token = jwt.CreateAccessToken("user-123", "player");

        // Flip a character in the payload segment to break the signature.
        var parts = token.Split('.');
        parts[1] = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        var tampered = string.Join('.', parts);

        Assert.Null(jwt.ValidateToken(tampered, "access"));
    }

    [Fact]
    public void Token_SignedWithDifferentSecret_ReturnsNull()
    {
        var issuer = Build();
        var attacker = Build(secret: "a-totally-different-secret-key-0987654321!!");

        var token = issuer.CreateAccessToken("user-123", "super_admin");

        // Same token, validated by a service with a different signing key.
        Assert.Null(attacker.ValidateToken(token, "access"));
    }

    [Fact]
    public void ExpiredToken_ReturnsNull()
    {
        // Negative expiry => token is born already expired; ClockSkew is Zero
        // in JwtService so it rejects immediately and deterministically.
        var jwt = Build(accessMinutes: "-1");

        var token = jwt.CreateAccessToken("user-123", "player");

        Assert.Null(jwt.ValidateToken(token, "access"));
    }
}
