using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// Time the venue is not selling: maintenance, a holiday, a private event. Nothing can be
/// booked into it — not from the app, not at the counter, not as a recurring week.
///
/// The times are Amman wall-clock times, naive, exactly like a booking's date + start time:
/// every slot decision in this system is made in venue-local time, so the block is too.
/// </summary>
[Table("venue_blocks")]
public class VenueBlock
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = "blk_" + Guid.NewGuid().ToString("N")[..12];

    [Column("venue_id")]
    [MaxLength(32)]
    public string VenueId { get; set; } = "";

    /// <summary>The one pitch that is closed, or null for the whole venue.</summary>
    [Column("pitch_id")]
    [MaxLength(64)]
    public string? PitchId { get; set; }

    [Column("starts_at")]
    public DateTime StartsAt { get; set; }

    /// <summary>Exclusive: a block to 18:00 leaves an 18:00 booking free.</summary>
    [Column("ends_at")]
    public DateTime EndsAt { get; set; }

    [Column("reason")]
    [MaxLength(200)]
    public string? Reason { get; set; }

    [Column("created_by_user_id")]
    [MaxLength(32)]
    public string? CreatedByUserId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Venue Venue { get; set; } = null!;
}
