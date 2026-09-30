using System.Text.Json.Serialization;
using SportsVenueApi.DTOs.Staff;

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

    /// <summary>The venue_owner a staff account works for. Null for every other role.</summary>
    [JsonPropertyName("managedByOwnerId")]
    public string? ManagedByOwnerId { get; set; }

    /// <summary>Staff only: the company role they hold.</summary>
    [JsonPropertyName("staffRole")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StaffRoleRef? StaffRole { get; set; }

    /// <summary>Staff only: true = every venue of the company.</summary>
    [JsonPropertyName("allVenues")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllVenues { get; set; }

    /// <summary>Staff only: the venues they are limited to, when not all.</summary>
    [JsonPropertyName("venueIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? VenueIds { get; set; }

    /// <summary>GET /users/me only: what the signed-in user may do in the back office.</summary>
    [JsonPropertyName("access")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AccessSummary? Access { get; set; }

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

    /// <summary>
    /// Required when a super_admin creates a venue_staff account. Ignored when an owner
    /// creates staff — they always get themselves, so an owner cannot plant staff inside
    /// a competitor's account.
    /// </summary>
    [JsonPropertyName("managedByOwnerId")]
    public string? ManagedByOwnerId { get; set; }

    /// <summary>Staff only. Omitted → the company's "View only" (or "Front desk" for write).</summary>
    [JsonPropertyName("staffRoleId")]
    public string? StaffRoleId { get; set; }

    /// <summary>Staff only. Omitted → all venues.</summary>
    [JsonPropertyName("allVenues")]
    public bool? AllVenues { get; set; }

    [JsonPropertyName("venueIds")]
    public List<string>? VenueIds { get; set; }
}

public class PermissionsUpdateRequest
{
    [JsonPropertyName("permissions")]
    public string Permissions { get; set; } = "";
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

/// <summary>
/// Returned once, by POST /users/{userId}/reset-password. The plaintext exists only in this
/// response — it is bcrypt-hashed before the row is saved and is never logged, so if the
/// admin loses it the only remedy is another reset.
/// </summary>
public class ResetPasswordResponse
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = "";

    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("temporaryPassword")]
    public string TemporaryPassword { get; set; } = "";
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
