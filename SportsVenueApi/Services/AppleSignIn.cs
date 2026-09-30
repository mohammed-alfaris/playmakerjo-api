using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace SportsVenueApi.Services;

/// <summary>Where Apple's public signing keys come from. Swapped for a local key in tests.</summary>
public interface IAppleKeySource
{
    Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken ct = default);
}

/// <summary>
/// Apple's published keys (https://appleid.apple.com/auth/keys), cached for a day. Apple rotates
/// them rarely and always publishes the new key before signing with it; a token signed with a
/// key not in the cache triggers one early refetch.
/// </summary>
public sealed class AppleKeySource : IAppleKeySource
{
    private const string KeysUrl = "https://appleid.apple.com/auth/keys";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyCollection<SecurityKey>? _keys;
    private static DateTime _fetchedAt;

    private readonly HttpClient _http;

    public AppleKeySource(HttpClient http) => _http = http;

    public async Task<IReadOnlyCollection<SecurityKey>> GetKeysAsync(CancellationToken ct = default)
    {
        if (_keys != null && DateTime.UtcNow - _fetchedAt < TimeSpan.FromHours(24)) return _keys;
        return await RefreshAsync(ct);
    }

    public async Task<IReadOnlyCollection<SecurityKey>> RefreshAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var json = await _http.GetStringAsync(KeysUrl, ct);
            _keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
            _fetchedAt = DateTime.UtcNow;
            return _keys;
        }
        finally { Gate.Release(); }
    }
}

/// <summary>Who an Apple identity token says the person is.</summary>
public sealed record AppleIdentity(string Subject, string? Email, bool EmailVerified);

/// <summary>
/// Checks an identity token from "Sign in with Apple" on the device: signed by Apple, issued
/// by Apple, meant for this app (the audience is the bundle id), and not expired.
/// </summary>
public sealed class AppleIdentityValidator
{
    public const string Issuer = "https://appleid.apple.com";

    private readonly IAppleKeySource _keys;
    private readonly string[] _audiences;
    private readonly ILogger<AppleIdentityValidator> _logger;

    public AppleIdentityValidator(IAppleKeySource keys, IConfiguration config, ILogger<AppleIdentityValidator> logger)
    {
        _keys = keys;
        _logger = logger;
        // The iOS bundle id; a web Services ID can be added later, comma-separated.
        _audiences = (config["Apple:Audiences"] ?? "com.playmakerjo.app")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<AppleIdentity?> ValidateAsync(string identityToken, CancellationToken ct = default)
    {
        var keys = await _keys.GetKeysAsync(ct);
        var parameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudiences = _audiences,
            IssuerSigningKeys = keys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            return Read(handler.ValidateToken(identityToken, parameters, out _));
        }
        catch (SecurityTokenSignatureKeyNotFoundException) when (_keys is AppleKeySource live)
        {
            // Apple may have rotated its keys since we cached them: fetch once more and retry.
            parameters.IssuerSigningKeys = await live.RefreshAsync(ct);
            try
            {
                return Read(handler.ValidateToken(identityToken, parameters, out _));
            }
            catch (SecurityTokenException ex) { _logger.LogWarning(ex, "Apple identity token rejected"); return null; }
        }
        catch (SecurityTokenException ex)
        {
            _logger.LogWarning(ex, "Apple identity token rejected");
            return null;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "Apple identity token malformed");
            return null;
        }
    }

    private static AppleIdentity? Read(System.Security.Claims.ClaimsPrincipal principal)
    {
        var sub = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub)) return null;
        var email = principal.FindFirst("email")?.Value;
        // Apple sends "true" as a string or a boolean depending on the flow.
        var verified = string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        return new AppleIdentity(sub, string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant(), verified);
    }
}
