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

    /// <summary>
    /// Only for the mobile app (X-Client: mobile), which has no cookie jar and keeps it in the
    /// device's secure storage. Browsers never see it: theirs stays in the httpOnly cookie,
    /// out of reach of page scripts.
    /// </summary>
    [JsonPropertyName("refreshToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; set; }
}

public class TokenData
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = "";

    /// <summary>A fresh refresh token for the mobile app: each refresh extends the session.</summary>
    [JsonPropertyName("refreshToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RefreshToken { get; set; }
}

public class RefreshRequest
{
    /// <summary>The mobile app sends its refresh token here; browsers send the cookie instead.</summary>
    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; set; }
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

public class DeleteAccountRequest
{
    /// <summary>Required when the account has a password; Google-only accounts have none.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("password")]
    public string? Password { get; set; }
}

public class AppleSignInRequest
{
    /// <summary>The identity token (a JWT) from Sign in with Apple on the device.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("identityToken")]
    public string IdentityToken { get; set; } = "";

    /// <summary>
    /// Apple gives the person's name to the app only on the very first sign-in, never in the
    /// token, so the app forwards it here when it has it.
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("name")]
    public string? Name { get; set; }
}
