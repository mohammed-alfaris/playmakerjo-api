using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Notifications;

public class NotificationResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("referenceId")]
    public string? ReferenceId { get; set; }

    [JsonPropertyName("isRead")]
    public bool IsRead { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

public class NotificationListResponse
{
    [JsonPropertyName("notifications")]
    public List<NotificationResponse> Notifications { get; set; } = [];

    [JsonPropertyName("unreadCount")]
    public int UnreadCount { get; set; }
}

public class RegisterDeviceTokenRequest
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";  // "android", "ios", "web"
}

// ── Admin: send notification ──────────────────────────────────────────────

public class AdminSendNotificationRequest
{
    [JsonPropertyName("userIds")]
    public List<string> UserIds { get; set; } = [];

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "general";

    [JsonPropertyName("image")]
    public string? Image { get; set; }
}

public class UserWithFcmResponse
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

    [JsonPropertyName("hasFcm")]
    public bool HasFcm { get; set; }

    [JsonPropertyName("fcmPlatforms")]
    public List<string> FcmPlatforms { get; set; } = [];
}

// ── Notification templates ────────────────────────────────────────────────

public class CreateTemplateRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "general";
}

public class UpdateTemplateRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class TemplateResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}
