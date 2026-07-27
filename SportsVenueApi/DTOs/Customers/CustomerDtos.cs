using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Customers;

/// <summary>
/// Everything the owner learns about a customer, all derived from the bookings table at
/// read time. Nothing here is a stored counter: a denormalised count that drifts from the
/// bookings it claims to summarise destroys trust in every other number on the screen, and
/// at this scale (~1,800 bookings per pitch per year) there is no performance reason to
/// risk it.
/// </summary>
public class CustomerStats
{
    /// <summary>Everything except cancellations — the size of the relationship.</summary>
    [JsonPropertyName("totalBookings")]
    public int TotalBookings { get; set; }

    /// <summary>
    /// Turned up. Explicitly completed, OR a past booking still sitting at "confirmed"
    /// that nobody flagged as a no-show.
    ///
    /// That second clause is what makes this number exist at all. "completed" is only ever
    /// written by a manual tap, and an owner has no reason to tap when everything went
    /// fine — so counting only "completed" would show 0 attended for every customer
    /// forever. Absence is declared; presence is assumed.
    /// </summary>
    [JsonPropertyName("attended")]
    public int Attended { get; set; }

    /// <summary>Confirmed and still in the future — what they have booked next.</summary>
    [JsonPropertyName("upcoming")]
    public int Upcoming { get; set; }

    /// <summary>Explicitly marked absent by a human. Never inferred.</summary>
    [JsonPropertyName("noShow")]
    public int NoShow { get; set; }

    [JsonPropertyName("cancelled")]
    public int Cancelled { get; set; }

    /// <summary>Live bookings where the money has not fully arrived.</summary>
    [JsonPropertyName("unpaid")]
    public int Unpaid { get; set; }

    /// <summary>Outstanding balance across those unpaid bookings, in JOD.</summary>
    [JsonPropertyName("amountOwed")]
    public double AmountOwed { get; set; }

    /// <summary>Booked at the counter or by phone.</summary>
    [JsonPropertyName("viaCounter")]
    public int ViaCounter { get; set; }

    /// <summary>Booked themselves through the player app.</summary>
    [JsonPropertyName("viaApp")]
    public int ViaApp { get; set; }

    /// <summary>Most recent booking that has already happened. Null if they never came.</summary>
    [JsonPropertyName("lastVisit")]
    public string? LastVisit { get; set; }

    /// <summary>Their first booking with this owner.</summary>
    [JsonPropertyName("customerSince")]
    public string? CustomerSince { get; set; }

    [JsonPropertyName("daysSinceLastVisit")]
    public int? DaysSinceLastVisit { get; set; }

    /// <summary>
    /// noShow / (attended + noShow), 0-100. Far more honest than the raw count: 3 misses
    /// out of 5 is a different person from 3 out of 50.
    /// </summary>
    [JsonPropertyName("noShowRate")]
    public double NoShowRate { get; set; }

    /// <summary>4+ bookings in the last 90 days.</summary>
    [JsonPropertyName("isRegular")]
    public bool IsRegular { get; set; }

    /// <summary>
    /// Came at least twice and has not been seen for over 30 days. This is the one that
    /// makes money — it is the only way an owner finds out a weekly regular quietly
    /// stopped coming, which a paper diary can never tell him.
    /// </summary>
    [JsonPropertyName("isLapsed")]
    public bool IsLapsed { get; set; }

    /// <summary>Only ever booked once.</summary>
    [JsonPropertyName("isNew")]
    public bool IsNew { get; set; }

    /// <summary>2+ no-shows, or a no-show rate above 25%. The red chip before you give away the slot.</summary>
    [JsonPropertyName("isUnreliable")]
    public bool IsUnreliable { get; set; }
}

public class CustomerResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("stats")]
    public CustomerStats Stats { get; set; } = new();
}

/// <summary>What the booking screen shows the moment a known number is typed.</summary>
public class CustomerLookupResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("stats")]
    public CustomerStats Stats { get; set; } = new();
}

public class CustomerBookingItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("venueName")]
    public string VenueName { get; set; } = "";

    [JsonPropertyName("sport")]
    public string? Sport { get; set; }

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("startTime")]
    public string? StartTime { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("totalAmount")]
    public double TotalAmount { get; set; }

    [JsonPropertyName("amountPaid")]
    public double AmountPaid { get; set; }

    [JsonPropertyName("isManual")]
    public bool IsManual { get; set; }
}

public class CustomerDetailResponse : CustomerResponse
{
    [JsonPropertyName("recentBookings")]
    public List<CustomerBookingItem> RecentBookings { get; set; } = [];
}

public class TopCustomerItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    /// <summary>Visits inside the reported period.</summary>
    [JsonPropertyName("visits")]
    public int Visits { get; set; }

    [JsonPropertyName("noShow")]
    public int NoShow { get; set; }

    [JsonPropertyName("lastVisit")]
    public string? LastVisit { get; set; }

    [JsonPropertyName("daysSinceLastVisit")]
    public int? DaysSinceLastVisit { get; set; }

    /// <summary>Lifetime visits, so a new face is distinguishable from an old regular.</summary>
    [JsonPropertyName("lifetimeVisits")]
    public int LifetimeVisits { get; set; }
}

public class CustomerMonthPoint
{
    /// <summary>"2026-07".</summary>
    [JsonPropertyName("month")]
    public string Month { get; set; } = "";

    [JsonPropertyName("newCustomers")]
    public int NewCustomers { get; set; }

    [JsonPropertyName("returningCustomers")]
    public int ReturningCustomers { get; set; }
}

/// <summary>
/// The month in one screen: how many people came, how many were new, how many came back,
/// who the best ones are, and who has gone quiet.
///
/// The number that matters most is <see cref="Returning"/>. New customers cost money to
/// find; returning ones are the business. A month with lots of new faces and no returns is
/// a leaking bucket, and nothing else in the product would show that.
/// </summary>
public class CustomerReportResponse
{
    [JsonPropertyName("month")]
    public string Month { get; set; } = "";

    /// <summary>Everyone in the book, all time.</summary>
    [JsonPropertyName("totalCustomers")]
    public int TotalCustomers { get; set; }

    /// <summary>Distinct customers who had a booking this month.</summary>
    [JsonPropertyName("active")]
    public int Active { get; set; }

    /// <summary>Of those, the ones whose very first booking was this month.</summary>
    [JsonPropertyName("newCustomers")]
    public int NewCustomers { get; set; }

    /// <summary>Of those, the ones who had booked before this month.</summary>
    [JsonPropertyName("returning")]
    public int Returning { get; set; }

    /// <summary>returning / active, 0-100.</summary>
    [JsonPropertyName("returnRate")]
    public double ReturnRate { get; set; }

    [JsonPropertyName("visits")]
    public int Visits { get; set; }

    [JsonPropertyName("noShows")]
    public int NoShows { get; set; }

    /// <summary>Customers who came at least twice and have not been seen for 30+ days.</summary>
    [JsonPropertyName("lapsedCount")]
    public int LapsedCount { get; set; }

    [JsonPropertyName("topCustomers")]
    public List<TopCustomerItem> TopCustomers { get; set; } = [];

    /// <summary>The win-back list — who to message, most recently lost first.</summary>
    [JsonPropertyName("lapsed")]
    public List<TopCustomerItem> Lapsed { get; set; } = [];

    [JsonPropertyName("trend")]
    public List<CustomerMonthPoint> Trend { get; set; } = [];
}

public class UpdateCustomerRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public class AttachCustomerRequest
{
    [JsonPropertyName("phone")]
    public string Phone { get; set; } = "";

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
