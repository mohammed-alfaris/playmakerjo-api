using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Users;

public class UserResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("permissions")]
    public string? Permissions { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

public class CreateUserRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "player";

    [JsonPropertyName("permissions")]
    public string? Permissions { get; set; }
}

public class ChangePasswordRequest
{
    [JsonPropertyName("currentPassword")]
    public string CurrentPassword { get; set; } = "";

    [JsonPropertyName("newPassword")]
    public string NewPassword { get; set; } = "";
}

public class StatusUpdateRequest
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

public class RoleUpdateRequest
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
}

public class AvatarUpdateRequest
{
    [JsonPropertyName("avatar")]
    public string Avatar { get; set; } = "";
}
