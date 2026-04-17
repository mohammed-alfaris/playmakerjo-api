using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("bookings")]
public class Booking
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("venue_id")]
    [MaxLength(32)]
    public string VenueId { get; set; } = "";

    [Column("player_id")]
    [MaxLength(32)]
    public string PlayerId { get; set; } = "";

    [Column("sport")]
    [MaxLength(100)]
    public string? Sport { get; set; }

    [Column("date")]
    public DateTime Date { get; set; }

    [Column("start_time")]
    [MaxLength(10)]
    public string? StartTime { get; set; }  // "08:00", "14:30" etc.

    [Column("duration")]
    public int Duration { get; set; } = 60;

    [Column("amount")]
    public double Amount { get; set; }

    [Column("total_amount")]
    public double TotalAmount { get; set; }

    [Column("deposit_amount")]
    public double DepositAmount { get; set; }

    [Column("deposit_paid")]
    public bool DepositPaid { get; set; } = false;

    [Column("amount_paid")]
    public double AmountPaid { get; set; } = 0;

    [Column("system_fee_percentage")]
    public double SystemFeePercentage { get; set; } = 5.0;

    [Column("system_fee")]
    public double SystemFee { get; set; }

    [Column("owner_amount")]
    public double OwnerAmount { get; set; }

    [Column("payment_method")]
    [MaxLength(50)]
    public string? PaymentMethod { get; set; }  // "stripe" / "cliq"

    [Column("notes", TypeName = "text")]
    public string? Notes { get; set; }

    [Column("recurring_group_id")]
    [MaxLength(32)]
    public string? RecurringGroupId { get; set; }

    [Column("payment_proof", TypeName = "longtext")]
    public string? PaymentProof { get; set; }  // base64 image for CliQ proof

    [Column("payment_proof_status")]
    [MaxLength(50)]
    public string? PaymentProofStatus { get; set; }  // "pending_review", "approved", "rejected"

    [Column("payment_proof_note", TypeName = "text")]
    public string? PaymentProofNote { get; set; }  // rejection reason from owner

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "pending";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("VenueId")]
    public Venue Venue { get; set; } = null!;

    [ForeignKey("PlayerId")]
    public User Player { get; set; } = null!;
}
