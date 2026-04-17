using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("reviews")]
public class Review
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [Column("player_id")]
    [MaxLength(32)]
    [Required]
    public string PlayerId { get; set; } = "";

    [Column("venue_id")]
    [MaxLength(32)]
    [Required]
    public string VenueId { get; set; } = "";

    [Column("rating")]
    [Required]
    public int Rating { get; set; }

    [Column("comment", TypeName = "text")]
    public string? Comment { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Soft-delete flag for admin moderation (Phase 3).</summary>
    [Column("hidden")]
    public bool Hidden { get; set; } = false;

    [ForeignKey("PlayerId")]
    public User Player { get; set; } = null!;

    [ForeignKey("VenueId")]
    public Venue Venue { get; set; } = null!;
}
