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

    /// <summary>
    /// The account the booking sits under. On a counter booking this is the owner's own
    /// user — kept because it is what the record says, not because it is the payer.
    /// </summary>
    [JsonPropertyName("player")]
    public PaymentPlayerRef Player { get; set; } = null!;

    /// <summary>The venue-side customer, when the booking was matched to one.</summary>
    [JsonPropertyName("customer")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaymentPlayerRef? Customer { get; set; }

    /// <summary>
    /// Who actually paid, resolved server-side. Clients should print this rather than
    /// picking between the two refs above and getting it wrong in a fifth place.
    /// </summary>
    [JsonPropertyName("payerName")]
    public string PayerName { get; set; } = "";

    /// <summary>Who recorded it. Null on rows written before the ledger existed.</summary>
    [JsonPropertyName("recordedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaymentPlayerRef? RecordedBy { get; set; }

    [JsonPropertyName("venue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaymentPlayerRef? Venue { get; set; }

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    /// <summary>"deposit" | "balance" | "full"</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";
}

/// <summary>Headline figures for the ledger page, over the same filter as the list.</summary>
public class PaymentTotalsResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("total")]
    public double Total { get; set; }

    [JsonPropertyName("byMethod")]
    public Dictionary<string, double> ByMethod { get; set; } = new();
}
