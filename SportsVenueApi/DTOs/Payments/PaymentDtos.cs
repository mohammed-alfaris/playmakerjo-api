using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Payments;

public class PaymentPlayerRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class PaymentResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("bookingRef")]
    public string BookingRef { get; set; } = "";

    [JsonPropertyName("player")]
    public PaymentPlayerRef Player { get; set; } = null!;

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";
}
