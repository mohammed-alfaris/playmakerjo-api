using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Companies;

public class UsageInfo
{
    [JsonPropertyName("used")]
    public int Used { get; set; }

    /// <summary>Null means unlimited.</summary>
    [JsonPropertyName("max")]
    public int? Max { get; set; }
}

public class CompanyResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("nameAr")]
    public string? NameAr { get; set; }

    [JsonPropertyName("ownerName")]
    public string OwnerName { get; set; } = "";

    [JsonPropertyName("ownerEmail")]
    public string OwnerEmail { get; set; } = "";

    [JsonPropertyName("ownerStatus")]
    public string OwnerStatus { get; set; } = "";

    [JsonPropertyName("venues")]
    public UsageInfo Venues { get; set; } = new();

    /// <summary>Active staff only — suspended staff do not take a seat.</summary>
    [JsonPropertyName("staff")]
    public UsageInfo Staff { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

public class UpdateCompanyRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("nameAr")]
    public string? NameAr { get; set; }

    /// <summary>
    /// Admin only. When present, BOTH limits are set from it — so null can mean "unlimited"
    /// rather than "leave unchanged", which a flat nullable field could not tell apart.
    /// </summary>
    [JsonPropertyName("limits")]
    public LimitsRequest? Limits { get; set; }
}

public class LimitsRequest
{
    [JsonPropertyName("maxVenues")]
    public int? MaxVenues { get; set; }

    [JsonPropertyName("maxStaff")]
    public int? MaxStaff { get; set; }
}
