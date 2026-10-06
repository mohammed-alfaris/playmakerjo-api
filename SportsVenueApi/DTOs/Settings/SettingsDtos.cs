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

    /// <summary>Venue limit given to a new company. Null = unlimited.</summary>
    [JsonPropertyName("defaultMaxVenues")]
    public int? DefaultMaxVenues { get; set; }

    /// <summary>Active-staff limit given to a new company. Null = unlimited.</summary>
    [JsonPropertyName("defaultMaxStaff")]
    public int? DefaultMaxStaff { get; set; }

    [JsonPropertyName("billing")]
    public BillingDefaults Billing { get; set; } = new();

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// Admin update request — all fields optional (PATCH semantics).
/// </summary>
/// <summary>What a company without its own prices pays, and the terms every company gets.</summary>
public class BillingDefaults
{
    /// <summary>Monthly price of a venue with fewer than <see cref="LargeVenueMinPitches"/> pitches.</summary>
    [JsonPropertyName("priceSmallVenue")] public double PriceSmallVenue { get; set; }
    [JsonPropertyName("priceLargeVenue")] public double PriceLargeVenue { get; set; }
    [JsonPropertyName("largeVenueMinPitches")] public int LargeVenueMinPitches { get; set; }
    [JsonPropertyName("setupFee")] public double SetupFee { get; set; }
    [JsonPropertyName("trialDays")] public int TrialDays { get; set; }
    [JsonPropertyName("paymentTermsDays")] public int PaymentTermsDays { get; set; }
}

public class UpdateSettingsRequest
{
    /// <summary>When present, every billing default is set from it.</summary>
    [JsonPropertyName("billing")]
    public BillingDefaults? Billing { get; set; }

    /// <summary>
    /// When present, BOTH defaults are set from it (null = unlimited). Applies to companies
    /// created from now on; existing companies keep their own limits.
    /// </summary>
    [JsonPropertyName("defaultLimits")]
    public SportsVenueApi.DTOs.Companies.LimitsRequest? DefaultLimits { get; set; }

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
