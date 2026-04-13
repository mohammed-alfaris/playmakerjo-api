using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("recurring_booking_groups")]
public class RecurringBookingGroup
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("player_id")]
    [MaxLength(32)]
    public string PlayerId { get; set; } = "";

    [Column("venue_id")]
    [MaxLength(32)]
    public string VenueId { get; set; } = "";

    [Column("sport")]
    [MaxLength(100)]
    public string Sport { get; set; } = "";

    [Column("day_of_week")]
    public int DayOfWeek { get; set; }

    [Column("start_time")]
    [MaxLength(10)]
    public string StartTime { get; set; } = "";

    [Column("duration")]
    public int Duration { get; set; } = 1;

    [Column("recurrence_type")]
    [MaxLength(20)]
    public string RecurrenceType { get; set; } = "weekly"; // weekly|biweekly

    [Column("start_date")]
    public DateTime StartDate { get; set; }

    [Column("end_date")]
    public DateTime EndDate { get; set; }

    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "active"; // active|cancelled

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("PlayerId")]
    public User Player { get; set; } = null!;

    [ForeignKey("VenueId")]
    public Venue Venue { get; set; } = null!;
}
