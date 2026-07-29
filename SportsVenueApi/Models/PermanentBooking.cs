using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// Owner-managed standing reservation. Unlike <see cref="RecurringBookingGroup"/>
/// (which materialises one <see cref="Booking"/> per occurrence within a 3-month
/// window), a permanent booking is a virtual "always booked" rule keyed by
/// day-of-week + start-time + duration. It blocks the slot on every matching
/// weekday until the owner cancels it. No player, no payment, no occurrences.
/// </summary>
[Table("permanent_bookings")]
public class PermanentBooking
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = "pb_" + Guid.NewGuid().ToString("N")[..10];

    [Column("venue_id")]
    [MaxLength(32)]
    public string VenueId { get; set; } = "";

    [Column("pitch_id")]
    [MaxLength(64)]
    public string? PitchId { get; set; }   // null = legacy single-pitch venue

    [Column("pitch_size")]
    [MaxLength(8)]
    public string? PitchSize { get; set; } // null when pitch isn't subdividable

    [Column("sport")]
    [MaxLength(100)]
    public string? Sport { get; set; }

    [Column("day_of_week")]
    public int DayOfWeek { get; set; }     // 0=Sunday … 6=Saturday (matches DateTime.DayOfWeek)

    [Column("start_time")]
    [MaxLength(10)]
    public string StartTime { get; set; } = ""; // "HH:mm"

    [Column("duration")]
    public int Duration { get; set; } = 60;

    [Column("label", TypeName = "text")]
    public string? Label { get; set; }     // free-text, e.g. "Khalid weekly"

    [Column("label_ar", TypeName = "text")]
    public string? LabelAr { get; set; }

    /// <summary>
    /// The customer holding this standing slot, when known. This is where the owner's
    /// "Khalid every Tuesday" actually lives — until now it was only the free-text
    /// <see cref="Label"/>, so when a weekly regular quietly stopped coming there was
    /// nothing to notice it with. Nullable: legacy rows keep rendering from Label.
    /// </summary>
    [Column("customer_id")]
    [MaxLength(32)]
    public string? CustomerId { get; set; }

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "active"; // active|cancelled

    [Column("created_by_user_id")]
    [MaxLength(32)]
    public string CreatedByUserId { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("cancelled_at")]
    public DateTime? CancelledAt { get; set; }

    [ForeignKey("VenueId")]
    public Venue Venue { get; set; } = null!;

    /// <summary>
    /// The organiser. The column and its index existed from the start and nothing ever set
    /// or read them — there was no navigation property, so no query could even load one.
    /// </summary>
    [ForeignKey("CustomerId")]
    public Customer? Customer { get; set; }
}
