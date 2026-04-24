using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Bookings;

public class VenueRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("images")]
    public List<string> Images { get; set; } = [];

    [JsonPropertyName("cliqAlias")]
    public string? CliqAlias { get; set; }
}

public class PlayerRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class BookingResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("venue")]
    public VenueRef Venue { get; set; } = null!;

    [JsonPropertyName("player")]
    public PlayerRef Player { get; set; } = null!;

    [JsonPropertyName("sport")]
    public string? Sport { get; set; }

    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    [JsonPropertyName("pitchSize")]
    public string? PitchSize { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    [JsonPropertyName("totalAmount")]
    public double TotalAmount { get; set; }

    [JsonPropertyName("depositAmount")]
    public double DepositAmount { get; set; }

    [JsonPropertyName("depositPaid")]
    public bool DepositPaid { get; set; }

    [JsonPropertyName("amountPaid")]
    public double AmountPaid { get; set; }

    [JsonPropertyName("systemFee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SystemFee { get; set; }

    [JsonPropertyName("ownerAmount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? OwnerAmount { get; set; }

    [JsonPropertyName("systemFeePercentage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? SystemFeePercentage { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("paymentProof")]
    public string? PaymentProof { get; set; }

    [JsonPropertyName("paymentProofStatus")]
    public string? PaymentProofStatus { get; set; }

    [JsonPropertyName("paymentProofNote")]
    public string? PaymentProofNote { get; set; }

    [JsonPropertyName("recurringGroupId")]
    public string? RecurringGroupId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

public class UploadProofRequest
{
    [JsonPropertyName("paymentProof")]
    public string PaymentProof { get; set; } = "";  // base64 image
}

public class ReviewProofRequest
{
    [JsonPropertyName("approved")]
    public bool Approved { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }  // rejection reason
}

public class CreateBookingRequest
{
    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("sport")]
    public string Sport { get; set; } = "";

    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }  // Required on venues with >1 pitch for the chosen sport

    [JsonPropertyName("pitchSize")]
    public string? PitchSize { get; set; }  // "5" | "6" | "7" | "8" | "11" — required on subdividable pitches

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";  // "2025-04-10"

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "";  // "08:00"

    [JsonPropertyName("duration")]
    public int Duration { get; set; } = 1;

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }  // "stripe" / "cliq"

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Admin/owner-created walk-in booking. When true the booking is created
    /// as "confirmed" immediately and the platform fee is 0 (100% goes to the owner).
    /// Only honoured for super_admin or the venue's own owner.
    /// </summary>
    [JsonPropertyName("isManual")]
    public bool IsManual { get; set; }
}

public class CreateRecurringBookingRequest
{
    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("sport")]
    public string Sport { get; set; } = "";

    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    [JsonPropertyName("pitchSize")]
    public string? PitchSize { get; set; }

    [JsonPropertyName("startDate")]
    public string StartDate { get; set; } = "";

    [JsonPropertyName("endDate")]
    public string EndDate { get; set; } = "";

    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; } = 1;

    [JsonPropertyName("recurrenceType")]
    public string RecurrenceType { get; set; } = "weekly"; // weekly|biweekly

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("conflictPolicy")]
    public string ConflictPolicy { get; set; } = "skip"; // skip|fail
}

public class RecurringBookingResponse
{
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = "";

    [JsonPropertyName("created")]
    public List<BookingResponse> Created { get; set; } = [];

    [JsonPropertyName("skippedDates")]
    public List<string> SkippedDates { get; set; } = [];

    [JsonPropertyName("requestedCount")]
    public int RequestedCount { get; set; }
}

public class AvailableSlotsResponse
{
    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("operatingHours")]
    public OperatingHoursInfo? OperatingHours { get; set; }

    [JsonPropertyName("bookedSlots")]
    public List<BookedSlotInfo> BookedSlots { get; set; } = [];

    [JsonPropertyName("pricePerHour")]
    public double PricePerHour { get; set; }

    [JsonPropertyName("minDuration")]
    public int MinDuration { get; set; }

    [JsonPropertyName("maxDuration")]
    public int MaxDuration { get; set; }

    [JsonPropertyName("depositPercentage")]
    public double DepositPercentage { get; set; }

    [JsonPropertyName("parentSize")]
    public string? ParentSize { get; set; }

    [JsonPropertyName("offeredSizes")]
    public List<string> OfferedSizes { get; set; } = [];

    [JsonPropertyName("sizePrices")]
    public Dictionary<string, double> SizePrices { get; set; } = [];

    [JsonPropertyName("capacityUnits")]
    public int CapacityUnits { get; set; } = 1;

    /// <summary>
    /// Per-pitch availability for multi-pitch venues. When <c>pitchId</c> is
    /// specified on the request, this is omitted (the top-level fields describe
    /// that single pitch). Otherwise this carries availability for every pitch
    /// on the venue so the client can show pitch-by-pitch timeline/selection.
    /// </summary>
    [JsonPropertyName("pitches")]
    public List<PitchAvailability>? Pitches { get; set; }
}

public class PitchAvailability
{
    [JsonPropertyName("pitchId")]
    public string PitchId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sport")]
    public string Sport { get; set; } = "";

    [JsonPropertyName("parentSize")]
    public string? ParentSize { get; set; }

    [JsonPropertyName("offeredSizes")]
    public List<string> OfferedSizes { get; set; } = [];

    [JsonPropertyName("sizePrices")]
    public Dictionary<string, double> SizePrices { get; set; } = [];

    [JsonPropertyName("pricePerHour")]
    public double PricePerHour { get; set; }

    [JsonPropertyName("capacityUnits")]
    public int CapacityUnits { get; set; } = 1;

    [JsonPropertyName("operatingHours")]
    public OperatingHoursInfo? OperatingHours { get; set; }

    [JsonPropertyName("bookedSlots")]
    public List<BookedSlotInfo> BookedSlots { get; set; } = [];
}

public class OperatingHoursInfo
{
    [JsonPropertyName("open")]
    public string Open { get; set; } = "";

    [JsonPropertyName("close")]
    public string Close { get; set; } = "";
}

public class BookedSlotInfo
{
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("sport")]
    public string? Sport { get; set; }

    [JsonPropertyName("pitchId")]
    public string? PitchId { get; set; }

    [JsonPropertyName("pitchSize")]
    public string? PitchSize { get; set; }

    [JsonPropertyName("unitWeight")]
    public int UnitWeight { get; set; } = 1;
}
