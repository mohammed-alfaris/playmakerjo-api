using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Venues;

public class VenueBlockResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    /// <summary>Null when the whole venue is closed.</summary>
    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    /// <summary>Amman local time, "yyyy-MM-ddTHH:mm".</summary>
    [JsonPropertyName("startsAt")]
    public string StartsAt { get; set; } = "";

    [JsonPropertyName("endsAt")]
    public string EndsAt { get; set; } = "";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public class CreateVenueBlockRequest
{
    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    /// <summary>Amman local time, "yyyy-MM-ddTHH:mm".</summary>
    [JsonPropertyName("startsAt")]
    [Required]
    public string StartsAt { get; set; } = "";

    [JsonPropertyName("endsAt")]
    [Required]
    public string EndsAt { get; set; } = "";

    [JsonPropertyName("reason")]
    [StringLength(200)]
    public string? Reason { get; set; }
}

/// <summary>A booking already inside the new block — the block stands; the desk decides what to do with these.</summary>
public class BlockedBookingInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}

public class CreateVenueBlockResponse
{
    [JsonPropertyName("block")]
    public VenueBlockResponse Block { get; set; } = new();

    [JsonPropertyName("overlappingBookings")]
    public List<BlockedBookingInfo> OverlappingBookings { get; set; } = [];
}
