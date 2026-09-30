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

    /// <summary>
    /// The venue-side customer record, when one is known. Nullable and ADDITIVE — it sits
    /// beside <see cref="PlayerId"/> rather than replacing it.
    ///
    /// PlayerId stays non-nullable and keeps holding the owner's own id on manual rows.
    /// Making it nullable would mean auditing all 13 <c>.Include(b => b.Player)</c> sites and
    /// two unguarded <c>b.Player.Name</c> dereferences for no gain, and would break the path
    /// the mobile app will use. Bookings that arrive from the app simply carry a null
    /// CustomerId; bookings taken at the counter carry both.
    /// </summary>
    [Column("customer_id")]
    [MaxLength(32)]
    public string? CustomerId { get; set; }

    [Column("sport")]
    [MaxLength(100)]
    public string? Sport { get; set; }

    [Column("pitch_id")]
    [MaxLength(64)]
    public string? PitchId { get; set; }  // Multi-pitch: which physical pitch. NULL = legacy (implicit single pitch resolved by sport at read time). 64 chars to fit dashboard-minted full UUIDs (36) and the API's "p_xxxxxxxxxx" mint format.

    [Column("pitch_size")]
    [MaxLength(8)]
    public string? PitchSize { get; set; }  // "5" | "6" | "7" | "8" | "11". NULL = legacy single-size

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

    /// <summary>
    /// The standing weekly rule this booking was materialised from, if any.
    ///
    /// A PermanentBooking is a RULE ("every Tuesday 20:00, Mohammed"), not a booking. It
    /// blocks the slot but never becomes a row, so on its own there is nothing to take money
    /// against, nothing to mark attended, and nothing that reaches the ledger or the
    /// customer's history. Recording a week creates a real booking and points it back here.
    ///
    /// Load-bearing: once a week is materialised the parent rule must STOP blocking that
    /// date, or the slot is consumed twice — invisible on a single-capacity pitch, but on a
    /// subdividable one it eats two units instead of one.
    /// </summary>
    [Column("permanent_booking_id")]
    [MaxLength(32)]
    public string? PermanentBookingId { get; set; }

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

    /// <summary>
    /// Taken at the counter / by phone rather than through the player app.
    ///
    /// This used to exist only as a request flag, consumed at creation and then thrown
    /// away, so afterwards nothing could tell the two channels apart — you could only
    /// infer it from the fee being zero, which breaks the moment the platform fee is set
    /// to 0%. Recording it makes "how many of my customers have moved to the app" a real
    /// question the owner can answer.
    ///
    /// Rows created before this column existed default to false; historical channel data
    /// is simply not recoverable, and guessing it from the fee would be worse than absent.
    /// </summary>
    [Column("is_manual")]
    public bool IsManual { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this unpaid app booking loses its slot, as a UTC instant. NULL means IMMUNE —
    /// permanently, not "not yet due".
    ///
    /// This is the entire safety model of the expiry job, and it is deliberately the
    /// opposite of how such a job is usually written. The obvious approach is to infer
    /// eligibility at sweep time from status + age, but every one of those inferences is
    /// wrong for some legitimate row: a counter booking is MEANT to sit unpaid
    /// (BookingsController.cs:597), a rejected proof returns a booking to pending_payment
    /// with CreatedAt unchanged so "time in this state" is unknowable, and one abandoned
    /// recurring checkout is three months of rows sharing a single CreatedAt.
    ///
    /// So nothing is inferred. Only the app-booking path arms a row, and only while it is
    /// genuinely waiting on a customer to pay. Every historical row, every counter booking
    /// and every recurring occurrence carries NULL and is therefore safe BY CONSTRUCTION,
    /// not because a WHERE clause remembered to exclude them.
    ///
    /// Do not backfill this column. Doing so arms every row it touches.
    /// </summary>
    [Column("payment_deadline_at")]
    public DateTime? PaymentDeadlineAt { get; set; }

    /// <summary>
    /// Set when the expiry job — not a human — cancelled this booking.
    ///
    /// The job writes Status = "cancelled" because that is the ONLY literal that actually
    /// frees a slot: all five occupancy predicates across three controllers are written as
    /// <c>Status != "cancelled"</c> (BookingsController.cs:437, :1238; VenuesController.cs:310,
    /// :687; PermanentBookingsController.cs:307), so a new "expired" status would leave the
    /// pitch blocked forever while the job reported success.
    ///
    /// Reusing "cancelled" would otherwise collapse "the customer changed their mind" into
    /// "we timed them out" everywhere, including the per-customer cancelled count. This
    /// column is what keeps those two apart.
    /// </summary>
    [Column("auto_cancelled_at")]
    public DateTime? AutoCancelledAt { get; set; }

    [ForeignKey("VenueId")]
    public Venue Venue { get; set; } = null!;

    [ForeignKey("PlayerId")]
    public User Player { get; set; } = null!;

    /// <summary>Nullable by design — see <see cref="CustomerId"/>.</summary>
    [ForeignKey("CustomerId")]
    public Customer? Customer { get; set; }
}
