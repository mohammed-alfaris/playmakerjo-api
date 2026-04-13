using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("payments")]
public class Payment
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("booking_id")]
    [MaxLength(32)]
    public string BookingId { get; set; } = "";

    [Column("player_id")]
    [MaxLength(32)]
    public string PlayerId { get; set; } = "";

    [Column("amount")]
    public double Amount { get; set; }

    [Column("method")]
    [MaxLength(100)]
    public string? Method { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "pending";

    [Column("date")]
    public DateTime Date { get; set; } = DateTime.UtcNow;

    [ForeignKey("BookingId")]
    public Booking Booking { get; set; } = null!;

    [ForeignKey("PlayerId")]
    public User Player { get; set; } = null!;
}
