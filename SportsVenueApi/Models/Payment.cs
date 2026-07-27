using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// One movement of money, recorded at the moment it is believed to have happened.
///
/// This table is APPEND-ONLY. Nothing in the application updates or deletes a row —
/// <c>AppDbContext.SaveChangesAsync</c> throws if anything tries. That constraint is the
/// entire point: <c>bookings.amount_paid</c> is a mutable field that any code path can
/// overwrite, so on its own it can only ever answer "how much is paid now" — never "who
/// said so, when, and by what means". A booking showing 50 JOD paid tells you nothing
/// about whether that was one counter payment, a deposit plus a balance, or a mistake
/// someone silently corrected.
///
/// Each row records a DELTA, not a running total. The invariant that makes the ledger
/// trustworthy, and which PaymentLedgerTests asserts directly:
///
///     SUM(payments.amount WHERE booking_id = X) == bookings.amount_paid for X
///
/// If those two ever disagree, either a write path forgot to record, or something mutated
/// amount_paid behind the ledger's back. Both are bugs worth failing loudly for.
/// </summary>
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

    /// <summary>
    /// The account the booking was made under. For an app booking this is the player; for
    /// a counter booking it is the OWNER'S OWN id, because that is what the booking
    /// carries — which is precisely why it must not be presented as the payer. Use
    /// <see cref="CustomerId"/> for the human who actually handed over the money. Kept
    /// required so the existing FK and the twenty seeded rows stay valid.
    /// </summary>
    [Column("player_id")]
    [MaxLength(32)]
    public string PlayerId { get; set; } = "";

    /// <summary>The real payer when one is known. Null for older rows and app bookings.</summary>
    [Column("customer_id")]
    [MaxLength(32)]
    public string? CustomerId { get; set; }

    /// <summary>
    /// Denormalised from the booking so the ledger can be scoped to an owner's venues
    /// without joining through bookings on every read. Payments are immutable and a
    /// booking never changes venue, so this cannot drift.
    /// </summary>
    [Column("venue_id")]
    [MaxLength(32)]
    public string? VenueId { get; set; }

    /// <summary>
    /// Who performed the act that recorded this money — the owner, a member of their
    /// staff, or an admin. The audit half of the audit trail: without it the ledger says
    /// what happened but not who is answerable for it, and "who took the cash on Tuesday"
    /// is the question an owner most wants the system to answer.
    /// </summary>
    [Column("recorded_by_user_id")]
    [MaxLength(32)]
    public string? RecordedByUserId { get; set; }

    /// <summary>The delta, in JOD. Positive: money arrived.</summary>
    [Column("amount")]
    public double Amount { get; set; }

    /// <summary>"cliq" | "cash" | "stripe" — how the money moved.</summary>
    [Column("method")]
    [MaxLength(100)]
    public string? Method { get; set; }

    /// <summary>"deposit" | "full" | "balance" — what this delta settled.</summary>
    [Column("kind")]
    [MaxLength(20)]
    public string Kind { get; set; } = "full";

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "pending";

    /// <summary>Free text describing the act, e.g. "CliQ proof approved".</summary>
    [Column("note")]
    [MaxLength(255)]
    public string? Note { get; set; }

    [Column("date")]
    public DateTime Date { get; set; } = DateTime.UtcNow;

    [ForeignKey("BookingId")]
    public Booking Booking { get; set; } = null!;

    [ForeignKey("PlayerId")]
    public User Player { get; set; } = null!;

    [ForeignKey("CustomerId")]
    public Customer? Customer { get; set; }

    [ForeignKey("RecordedByUserId")]
    public User? RecordedBy { get; set; }
}
