using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Reports;

public class SummaryResponse
{
    [JsonPropertyName("totalRevenue")]
    public double TotalRevenue { get; set; }

    [JsonPropertyName("ownerRevenue")]
    public double OwnerRevenue { get; set; }

    [JsonPropertyName("systemRevenue")]
    public double SystemRevenue { get; set; }

    [JsonPropertyName("platformFeePercentage")]
    public double PlatformFeePercentage { get; set; }

    [JsonPropertyName("totalBookings")]
    public int TotalBookings { get; set; }

    [JsonPropertyName("totalVenues")]
    public int TotalVenues { get; set; }

    [JsonPropertyName("totalUsers")]
    public int TotalUsers { get; set; }

    [JsonPropertyName("revenueChange")]
    public double RevenueChange { get; set; }

    [JsonPropertyName("bookingsChange")]
    public double BookingsChange { get; set; }

    [JsonPropertyName("venuesChange")]
    public double VenuesChange { get; set; }

    [JsonPropertyName("usersChange")]
    public double UsersChange { get; set; }
}

public class RevenueChartPoint
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("revenue")]
    public double Revenue { get; set; }

    [JsonPropertyName("ownerRevenue")]
    public double OwnerRevenue { get; set; }

    [JsonPropertyName("systemRevenue")]
    public double SystemRevenue { get; set; }
}

public class TopVenueItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("revenue")]
    public double Revenue { get; set; }

    [JsonPropertyName("ownerRevenue")]
    public double OwnerRevenue { get; set; }

    [JsonPropertyName("systemRevenue")]
    public double SystemRevenue { get; set; }
}

public class SportBreakdownItem
{
    [JsonPropertyName("sport")]
    public string Sport { get; set; } = "";

    [JsonPropertyName("count")]
    public int Count { get; set; }
}
