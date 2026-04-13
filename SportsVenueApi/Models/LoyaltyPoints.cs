using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("loyalty_points")]
public class LoyaltyPoints
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("user_id")]
    [MaxLength(32)]
    public string UserId { get; set; } = "";

    [Column("venue_id")]
    [MaxLength(32)]
    public string VenueId { get; set; } = "";

    [Column("points")]
    public int Points { get; set; } = 0;

    [Column("total_redeemed")]
    public int TotalRedeemed { get; set; } = 0;

    [ForeignKey("UserId")]
    public User User { get; set; } = null!;

    [ForeignKey("VenueId")]
    public Venue Venue { get; set; } = null!;
}
