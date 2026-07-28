using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.PermanentBookings;

/// <summary>The person behind a standing weekly reservation.</summary>
public class PermanentCustomerRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";
}

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

    [JsonPropertyName("sport")]
    public string? Sport { get; set; }

    [JsonPropertyName("dayOfWeek")]
    public int DayOfWeek { get; set; }

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("labelAr")]
    public string? LabelAr { get; set; }

    /// <summary>The organiser, when one was captured. Null on rows created before this existed.</summary>
    [JsonPropertyName("customer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PermanentCustomerRef? Customer { get; set; }

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

    [JsonPropertyName("labelAr")]
    public string? LabelAr { get; set; }

    /// <summary>
    /// The organiser's mobile. Optional, but this is the single most valuable number a
    /// venue can hold — standing groups are roughly 40% of bookings and, until now, none of
    /// their organisers existed in the customer book at all.
    /// </summary>
    [JsonPropertyName("customerPhone")]
    public string? CustomerPhone { get; set; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }
}
