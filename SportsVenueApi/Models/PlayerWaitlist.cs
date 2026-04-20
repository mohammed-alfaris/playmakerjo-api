using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("player_waitlist")]
public class PlayerWaitlist
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("email")]
    [MaxLength(255)]
    public string Email { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
