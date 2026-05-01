using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.PermanentBookings;

public class PermanentBookingDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    [JsonPropertyName("pitchSize")]
    public string? PitchSize { get; set; }

    [JsonPropertyName("dayOfWeek")]
    public int DayOfWeek { get; set; }

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("createdByUserId")]
    public string CreatedByUserId { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("cancelledAt")]
    public DateTime? CancelledAt { get; set; }
}

public class CreatePermanentBookingRequest
{
    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    [JsonPropertyName("pitchSize")]
    public string? PitchSize { get; set; }

    [JsonPropertyName("dayOfWeek")]
    public int DayOfWeek { get; set; }

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; } = 60;

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}
