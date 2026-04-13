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

    public string CreateAccessToken(string userId, string role)
    {
        return CreateToken(userId, role, "access", TimeSpan.FromMinutes(AccessMinutes));
    }

    public string CreateRefreshToken(string userId, string role)
    {
        return CreateToken(userId, role, "refresh", TimeSpan.FromDays(RefreshDays));
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
                ValidIssuer = "YallaNhjez",
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

    private string CreateToken(string userId, string role, string type, TimeSpan expiry)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim("role", role),
            new Claim("type", type)
        };

        var token = new JwtSecurityToken(
            issuer: "YallaNhjez",
            claims: claims,
            expires: DateTime.UtcNow.Add(expiry),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
