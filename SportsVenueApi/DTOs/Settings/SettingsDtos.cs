using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Settings;

/// <summary>
/// Full settings payload — returned from GET /settings and PATCH /settings (admin).
/// </summary>
public class SettingsResponse
{
    [JsonPropertyName("platformFeePercentage")]
    public double PlatformFeePercentage { get; set; }

    [JsonPropertyName("maintenanceMode")]
    public bool MaintenanceMode { get; set; }

    [JsonPropertyName("maintenanceMessageEn")]
    public string MaintenanceMessageEn { get; set; } = "";

    [JsonPropertyName("maintenanceMessageAr")]
    public string MaintenanceMessageAr { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// Admin update request — all fields optional (PATCH semantics).
/// </summary>
public class UpdateSettingsRequest
{
    [JsonPropertyName("platformFeePercentage")]
    public double? PlatformFeePercentage { get; set; }

    [JsonPropertyName("maintenanceMode")]
    public bool? MaintenanceMode { get; set; }

    [JsonPropertyName("maintenanceMessageEn")]
    public string? MaintenanceMessageEn { get; set; }

    [JsonPropertyName("maintenanceMessageAr")]
    public string? MaintenanceMessageAr { get; set; }
}

/// <summary>
/// Minimal public payload returned by GET /platform/status (no auth).
/// Used by the mobile app to decide whether to show the maintenance screen.
/// </summary>
public class PlatformStatusResponse
{
    [JsonPropertyName("maintenanceMode")]
    public bool MaintenanceMode { get; set; }

    [JsonPropertyName("maintenanceMessageEn")]
    public string MaintenanceMessageEn { get; set; } = "";

    [JsonPropertyName("maintenanceMessageAr")]
    public string MaintenanceMessageAr { get; set; } = "";
}
