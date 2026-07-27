using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Bookings;

public class VenueRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("nameAr")]
    public string? NameAr { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("cityAr")]
    public string? CityAr { get; set; }

    [JsonPropertyName("images")]
    public List<string> Images { get; set; } = [];

    /// <summary>
    /// The venue's CliQ payment alias. Populated only for the venue's own side —
    /// owner, their staff, admin. Omitted from the payload entirely rather than
    /// sent as null, so a client cannot tell a stripped response from a venue
    /// that has not set one.
    /// </summary>
    [JsonPropertyName("cliqAlias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CliqAlias { get; set; }
}

public class PlayerRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class CustomerRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";
}

public class BookingResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("venue")]
    public VenueRef Venue { get; set; } = null!;

    [JsonPropertyName("player")]
    public PlayerRef Player { get; set; } = null!;

    /// <summary>
    /// The venue-side customer, when one was recorded. Null for app bookings and for
    /// walk-ins taken before customer records existed — so every consumer must fall back to
    /// <see cref="Player"/>, and on a legacy manual booking that fallback is the OWNER'S own
    /// name, which is exactly the display bug this field exists to end.
    /// </summary>
    [JsonPropertyName("customer")]
    public CustomerRef? Customer { get; set; }

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

    /// <summary>Taken at the counter or by phone, rather than through the player app.</summary>
    [JsonPropertyName("isManual")]
    public bool IsManual { get; set; }

    /// <summary>
    /// When this unpaid hold is released, if it is one. Null on everything else — counter
    /// bookings, recurring series, anything already paid or awaiting proof review.
    /// </summary>
    [JsonPropertyName("paymentDeadlineAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaymentDeadlineAt { get; set; }

    /// <summary>
    /// Set when the expiry job released this booking rather than a person cancelling it.
    /// Both write status "cancelled" — this is the only thing that tells them apart, so a
    /// client must read it before printing "cancelled" to an owner.
    /// </summary>
    [JsonPropertyName("autoCancelledAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoCancelledAt { get; set; }

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
    /// Only honoured for super_admin, the venue's own owner, or their staff with "write".
    /// </summary>
    [JsonPropertyName("isManual")]
    public bool IsManual { get; set; }

    /// <summary>
    /// Who the booking is for, as typed at the counter. Honoured ONLY on the already
    /// authorised manual path — a player sending these is silently ignored, so nobody can
    /// write into someone else's customer book.
    ///
    /// A number that is not a recognisable Jordanian mobile is dropped rather than
    /// rejected: the booking must never fail because the person standing at the counter
    /// has a Syrian SIM or gave a landline.
    /// </summary>
    [JsonPropertyName("customerPhone")]
    public string? CustomerPhone { get; set; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    /// <summary>
    /// Manual bookings only: did the customer actually hand over the money now?
    ///
    /// Every manual booking used to be recorded as paid in full the instant it was created.
    /// That is right for someone standing at the counter with cash, and wrong for the more
    /// common case — a phone call on Sunday for a slot next Tuesday, paid on arrival. The
    /// consequence was that the most expensive kind of no-show (booked, never paid, never
    /// turned up) was recorded as fully settled, and revenue reports counted money that had
    /// not arrived.
    ///
    /// Defaults to true so existing callers keep their current behaviour.
    /// </summary>
    [JsonPropertyName("customerPaid")]
    public bool CustomerPaid { get; set; } = true;
}

public class AttendanceConfirmRequest
{
    /// <summary>The past bookings the owner is confirming everyone turned up for.</summary>
    [JsonPropertyName("bookingIds")]
    public List<string>? BookingIds { get; set; }
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
