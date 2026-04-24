using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Venues;

public class OwnerRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

/// <summary>
/// Per-sport configuration carried inside a venue's <c>sportsConfig</c> map.
/// All fields optional — a sport that doesn't override a field inherits from
/// the venue-level defaults (pricePerHour, operatingHours).
/// Split config (parentSize/subSizes/sizePrices) is only meaningful for
/// football.
/// </summary>
public class SportConfigDto
{
    [JsonPropertyName("pricePerHour")]
    public double? PricePerHour { get; set; }

    [JsonPropertyName("operatingHours")]
    public object? OperatingHours { get; set; }

    [JsonPropertyName("parentSize")]
    public string? ParentSize { get; set; }

    [JsonPropertyName("subSizes")]
    public List<string>? SubSizes { get; set; }

    [JsonPropertyName("sizePrices")]
    public Dictionary<string, double>? SizePrices { get; set; }
}

/// <summary>
/// One physical pitch inside a multi-pitch venue. Venues with an empty
/// <c>pitches</c> array behave as a single implicit pitch synthesised at
/// read time from the legacy venue-level fields (<c>sportsConfig</c>,
/// <c>parentSize</c>, <c>subSizes</c>). Subdivision still applies per-pitch
/// for football pitches — each pitch carries its own <c>parentSize</c>,
/// <c>subSizes</c>, and <c>sizePrices</c>.
/// </summary>
public class PitchDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sport")]
    public string Sport { get; set; } = "";

    [JsonPropertyName("parentSize")]
    public string? ParentSize { get; set; }

    [JsonPropertyName("subSizes")]
    public List<string> SubSizes { get; set; } = [];

    [JsonPropertyName("sizePrices")]
    public Dictionary<string, double> SizePrices { get; set; } = [];

    [JsonPropertyName("pricePerHour")]
    public double PricePerHour { get; set; }

    [JsonPropertyName("operatingHours")]
    public object? OperatingHours { get; set; }
}

public class VenueResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("owner")]
    public OwnerRef Owner { get; set; } = null!;

    [JsonPropertyName("sports")]
    public List<string> Sports { get; set; } = [];

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("pricePerHour")]
    public double PricePerHour { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("images")]
    public List<string> Images { get; set; } = [];

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("cliqAlias")]
    public string? CliqAlias { get; set; }

    [JsonPropertyName("operatingHours")]
    public object? OperatingHours { get; set; }

    [JsonPropertyName("minBookingDuration")]
    public int MinBookingDuration { get; set; } = 1;

    [JsonPropertyName("maxBookingDuration")]
    public int MaxBookingDuration { get; set; } = 3;

    [JsonPropertyName("depositPercentage")]
    public double DepositPercentage { get; set; } = 20.0;

    [JsonPropertyName("parentSize")]
    public string? ParentSize { get; set; }

    [JsonPropertyName("subSizes")]
    public List<string> SubSizes { get; set; } = [];

    [JsonPropertyName("sizePrices")]
    public Dictionary<string, double> SizePrices { get; set; } = [];

    [JsonPropertyName("sportsConfig")]
    public Dictionary<string, SportConfigDto> SportsConfig { get; set; } = [];

    [JsonPropertyName("sportsIsolated")]
    public bool SportsIsolated { get; set; }

    [JsonPropertyName("pitches")]
    public List<PitchDto> Pitches { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("averageRating")]
    public double? AverageRating { get; set; }

    [JsonPropertyName("reviewCount")]
    public int ReviewCount { get; set; }
}

public class VenueCreateRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("sports")]
    public List<string> Sports { get; set; } = [];

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("pricePerHour")]
    public double PricePerHour { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("images")]
    public List<string> Images { get; set; } = [];

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("cliqAlias")]
    public string? CliqAlias { get; set; }

    [JsonPropertyName("operatingHours")]
    public object? OperatingHours { get; set; }

    [JsonPropertyName("minBookingDuration")]
    public int? MinBookingDuration { get; set; }

    [JsonPropertyName("maxBookingDuration")]
    public int? MaxBookingDuration { get; set; }

    [JsonPropertyName("depositPercentage")]
    public double? DepositPercentage { get; set; }

    [JsonPropertyName("parentSize")]
    public string? ParentSize { get; set; }

    [JsonPropertyName("subSizes")]
    public List<string>? SubSizes { get; set; }

    [JsonPropertyName("sizePrices")]
    public Dictionary<string, double>? SizePrices { get; set; }

    [JsonPropertyName("sportsConfig")]
    public Dictionary<string, SportConfigDto>? SportsConfig { get; set; }

    [JsonPropertyName("sportsIsolated")]
    public bool? SportsIsolated { get; set; }

    [JsonPropertyName("pitches")]
    public List<PitchDto>? Pitches { get; set; }
}

public class VenueUpdateRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("owner_id")]
    public string? OwnerId { get; set; }

    [JsonPropertyName("sports")]
    public List<string>? Sports { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("pricePerHour")]
    public double? PricePerHour { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("cliqAlias")]
    public string? CliqAlias { get; set; }

    [JsonPropertyName("operatingHours")]
    public object? OperatingHours { get; set; }

    [JsonPropertyName("minBookingDuration")]
    public int? MinBookingDuration { get; set; }

    [JsonPropertyName("maxBookingDuration")]
    public int? MaxBookingDuration { get; set; }

    [JsonPropertyName("depositPercentage")]
    public double? DepositPercentage { get; set; }

    [JsonPropertyName("parentSize")]
    public string? ParentSize { get; set; }

    [JsonPropertyName("subSizes")]
    public List<string>? SubSizes { get; set; }

    [JsonPropertyName("sizePrices")]
    public Dictionary<string, double>? SizePrices { get; set; }

    [JsonPropertyName("sportsConfig")]
    public Dictionary<string, SportConfigDto>? SportsConfig { get; set; }

    [JsonPropertyName("sportsIsolated")]
    public bool? SportsIsolated { get; set; }

    [JsonPropertyName("pitches")]
    public List<PitchDto>? Pitches { get; set; }
}

public class VenueStatsResponse
{
    [JsonPropertyName("totalBookings")]
    public int TotalBookings { get; set; }

    [JsonPropertyName("totalRevenue")]
    public double TotalRevenue { get; set; }

    [JsonPropertyName("activeSince")]
    public string ActiveSince { get; set; } = "";
}
