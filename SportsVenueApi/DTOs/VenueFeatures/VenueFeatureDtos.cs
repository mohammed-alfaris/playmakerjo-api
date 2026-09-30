using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.VenueFeatures;

/// <summary>A catalog feature as the catalog endpoints return it.</summary>
public class VenueFeatureResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("nameAr")]
    public string NameAr { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>How many venues use it. Admin-only, so absent from the public payload.</summary>
    [JsonPropertyName("venueCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VenueCount { get; set; }
}

/// <summary>A catalog feature as it appears on a venue.</summary>
public class VenueFeatureRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("nameAr")]
    public string NameAr { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";
}

public class CreateVenueFeatureRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nameAr")]
    public string? NameAr { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("sortOrder")]
    public int? SortOrder { get; set; }
}

public class UpdateVenueFeatureRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nameAr")]
    public string? NameAr { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("sortOrder")]
    public int? SortOrder { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}
