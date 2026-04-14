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
