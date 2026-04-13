using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("loyalty_programs")]
public class LoyaltyProgram
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("venue_id")]
    [MaxLength(32)]
    public string VenueId { get; set; } = "";

    [Column("bookings_required")]
    public int BookingsRequired { get; set; } = 10;

    [Column("reward_type")]
    [MaxLength(50)]
    public string RewardType { get; set; } = "free_hour"; // free_hour, percentage_discount, fixed_discount

    [Column("reward_value")]
    public double RewardValue { get; set; } = 1; // hours, %, or JOD

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("VenueId")]
    public Venue Venue { get; set; } = null!;
}
