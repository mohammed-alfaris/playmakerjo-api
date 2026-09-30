using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Auth;

public class LoginRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class AuthUserResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    /// <summary>
    /// "read" | "write" for venue_staff, null for every other role.
    ///
    /// Login is the only place the dashboard learns who it is holding — it stores this
    /// response and never re-fetches the profile. Omitting the field meant
    /// <c>user.permissions</c> was undefined forever, useRole fell back to "read", and
    /// EVERY staff member saw a schedule with no action buttons regardless of what the
    /// owner had granted them. The counter clerk the whole role exists for could not
    /// take a booking.
    ///
    /// This is presentation only. The server decides for itself from the JWT and is the
    /// thing that actually enforces it; sending it here just stops the UI hiding
    /// buttons that would have worked.
    /// </summary>
    [JsonPropertyName("permissions")]
    public string? Permissions { get; set; }

    /// <summary>The venue_owner a staff member works for. Null for every other role.</summary>
    [JsonPropertyName("managedByOwnerId")]
    public string? ManagedByOwnerId { get; set; }
}

public class LoginData
{
    [JsonPropertyName("user")]
    public AuthUserResponse User { get; set; } = null!;

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";
}

public class TokenData
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";
}

public class RegisterRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class UpdateProfileRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("preferredLanguage")]
    public string? PreferredLanguage { get; set; }
}

public class UpdateLanguageRequest
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "en";
}

/// <summary>
/// Client sends the Google-issued ID token (JWT) obtained from
/// GoogleSignIn on the device. Server validates the token against
/// Google's public keys + expected audience, then looks up the user
/// by email.
/// </summary>
public class GoogleSignInRequest
{
    [JsonPropertyName("idToken")]
    public string IdToken { get; set; } = "";
}
