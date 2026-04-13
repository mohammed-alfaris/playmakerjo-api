using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("notifications")]
public class Notification
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [Column("user_id")]
    [MaxLength(32)]
    public string UserId { get; set; } = "";

    [Column("title")]
    [MaxLength(255)]
    public string Title { get; set; } = "";

    [Column("body", TypeName = "text")]
    public string Body { get; set; } = "";

    [Column("type")]
    [MaxLength(50)]
    public string Type { get; set; } = "";  // booking_confirmed, proof_received, proof_approved, proof_rejected, booking_cancelled

    [Column("reference_id")]
    [MaxLength(32)]
    public string? ReferenceId { get; set; }  // booking ID

    [Column("is_read")]
    public bool IsRead { get; set; } = false;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("UserId")]
    public User User { get; set; } = null!;
}
