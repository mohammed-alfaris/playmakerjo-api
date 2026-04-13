using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Loyalty;

// ── Requests ──

public class CreateLoyaltyProgramRequest
{
    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("bookingsRequired")]
    public int BookingsRequired { get; set; } = 10;

    [JsonPropertyName("rewardType")]
    public string RewardType { get; set; } = "free_hour";

    [JsonPropertyName("rewardValue")]
    public double RewardValue { get; set; } = 1;

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;
}

public class UpdateLoyaltyProgramRequest
{
    [JsonPropertyName("bookingsRequired")]
    public int? BookingsRequired { get; set; }

    [JsonPropertyName("rewardType")]
    public string? RewardType { get; set; }

    [JsonPropertyName("rewardValue")]
    public double? RewardValue { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}

public class RedeemLoyaltyRequest
{
    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";
}

// ── Responses ──

public class LoyaltyProgramResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("venueName")]
    public string VenueName { get; set; } = "";

    [JsonPropertyName("bookingsRequired")]
    public int BookingsRequired { get; set; }

    [JsonPropertyName("rewardType")]
    public string RewardType { get; set; } = "";

    [JsonPropertyName("rewardValue")]
    public double RewardValue { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

public class LoyaltyProgressResponse
{
    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("venueName")]
    public string VenueName { get; set; } = "";

    [JsonPropertyName("venueImage")]
    public string? VenueImage { get; set; }

    [JsonPropertyName("points")]
    public int Points { get; set; }

    [JsonPropertyName("bookingsRequired")]
    public int BookingsRequired { get; set; }

    [JsonPropertyName("rewardType")]
    public string RewardType { get; set; } = "";

    [JsonPropertyName("rewardValue")]
    public double RewardValue { get; set; }

    [JsonPropertyName("isEligible")]
    public bool IsEligible { get; set; }

    [JsonPropertyName("totalRedeemed")]
    public int TotalRedeemed { get; set; }
}
