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
