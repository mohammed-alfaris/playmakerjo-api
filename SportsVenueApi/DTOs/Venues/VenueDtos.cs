using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Venues;

public class OwnerRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
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

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
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
