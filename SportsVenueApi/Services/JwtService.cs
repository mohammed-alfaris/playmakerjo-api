using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace SportsVenueApi.Services;

public class JwtService
{
    private readonly IConfiguration _config;

    public JwtService(IConfiguration config)
    {
        _config = config;
    }

    private string SecretKey => _config["Jwt:SecretKey"]
        ?? throw new InvalidOperationException("Jwt:SecretKey is not configured.");
    private int AccessMinutes => int.Parse(_config["Jwt:AccessTokenExpireMinutes"] ?? "15");
    private int RefreshDays => int.Parse(_config["Jwt:RefreshTokenExpireDays"] ?? "7");

    public string CreateAccessToken(
        string userId, string role, string? managedByOwnerId = null, string? permissions = null)
    {
        return CreateToken(userId, role, "access", TimeSpan.FromMinutes(AccessMinutes),
            managedByOwnerId, permissions);
    }

    public string CreateRefreshToken(
        string userId, string role, string? managedByOwnerId = null, string? permissions = null)
    {
        return CreateToken(userId, role, "refresh", TimeSpan.FromDays(RefreshDays),
            managedByOwnerId, permissions);
    }

    public ClaimsPrincipal? ValidateToken(string token, string expectedType)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var handler = new JwtSecurityTokenHandler();

        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = "PlayMakerJO",
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero
            }, out var validatedToken);

            var jwtToken = (JwtSecurityToken)validatedToken;
            var type = jwtToken.Claims.FirstOrDefault(c => c.Type == "type")?.Value;

            if (type != expectedType) return null;

            return principal;
        }
        catch
        {
            return null;
        }
    }

    private string CreateToken(
        string userId, string role, string type, TimeSpan expiry,
        string? managedByOwnerId = null, string? permissions = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new("role", role),
            new("type", type),
            // Issued-at, written explicitly rather than left to the library. It is what lets
            // /auth/refresh refuse a token minted before the account's password changed —
            // the only way a password reset can end sessions that are already open, since a
            // refresh cookie is otherwise good for its full seven days. Set by hand because
            // the value has to be depended on: a claim that is merely usually present is not
            // something to hang session revocation off.
            new(JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        // venue_staff carry the owner they work for and their read/write level, so every
        // request can resolve "which venues may this person touch" without a DB lookup.
        // Absent for every other role — and deliberately absent for legacy staff rows that
        // have no owner, which the access helpers treat as no access rather than all.
        if (!string.IsNullOrEmpty(managedByOwnerId))
            claims.Add(new Claim("owner_id", managedByOwnerId));
        if (!string.IsNullOrEmpty(permissions))
            claims.Add(new Claim("permissions", permissions));

        var token = new JwtSecurityToken(
            issuer: "PlayMakerJO",
            claims: claims,
            expires: DateTime.UtcNow.Add(expiry),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
