using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// What PlayMaker bills a company for one month: its subscription (in advance) and the
/// commission on last month's app bookings (in arrears).
///
/// Drafted by the admin's Generate, then issued, then paid (or voided). A draft has no number:
/// numbers are given at issue, so a voided draft leaves no gap in the sequence.
/// </summary>
[Table("invoices")]
public class Invoice
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = "inv_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>"PMJ-2026-0001". Null while a draft.</summary>
    [Column("number")]
    [MaxLength(20)]
    public string? Number { get; set; }

    /// <summary>The company (its owner's user id, as everywhere else).</summary>
    [Column("owner_id")]
    [MaxLength(32)]
    public string OwnerId { get; set; } = "";

    /// <summary>The month billed, "yyyy-MM".</summary>
    [Column("period")]
    [MaxLength(7)]
    public string Period { get; set; } = "";

    /// <summary>"draft" | "issued" | "paid" | "void"</summary>
    [Column("status")]
    [MaxLength(10)]
    public string Status { get; set; } = "draft";

    [Column("total")]
    public double Total { get; set; }

    [Column("issued_at")]
    public DateTime? IssuedAt { get; set; }

    /// <summary>Amman calendar date after which an unpaid issued invoice is overdue.</summary>
    [Column("due_on")]
    public DateTime? DueOn { get; set; }

    [Column("paid_at")]
    public DateTime? PaidAt { get; set; }

    /// <summary>"cliq" | "bank_transfer" | "cash"</summary>
    [Column("paid_method")]
    [MaxLength(20)]
    public string? PaidMethod { get; set; }

    [Column("paid_reference")]
    [MaxLength(100)]
    public string? PaidReference { get; set; }

    [Column("void_reason")]
    [MaxLength(255)]
    public string? VoidReason { get; set; }

    [Column("created_by_user_id")]
    [MaxLength(32)]
    public string? CreatedByUserId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User Owner { get; set; } = null!;

    public List<InvoiceLine> Lines { get; set; } = [];
}

[Table("invoice_lines")]
public class InvoiceLine
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = "il_" + Guid.NewGuid().ToString("N")[..12];

    [Column("invoice_id")]
    [MaxLength(32)]
    public string InvoiceId { get; set; } = "";

    /// <summary>"subscription" | "extra_venues" | "setup_fee" | "commission" | "adjustment"</summary>
    [Column("kind")]
    [MaxLength(20)]
    public string Kind { get; set; } = "";

    [Column("description")]
    [MaxLength(255)]
    public string Description { get; set; } = "";

    [Column("description_ar")]
    [MaxLength(255)]
    public string? DescriptionAr { get; set; }

    [Column("quantity")]
    public double Quantity { get; set; } = 1;

    [Column("unit_price")]
    public double UnitPrice { get; set; }

    /// <summary>Quantity × unit price, rounded; negative for a discount.</summary>
    [Column("amount")]
    public double Amount { get; set; }

    /// <summary>
    /// For subscription lines, the service period paid for: [from, to). This is how an annual
    /// plan knows the next eleven months are already paid — and a voided invoice stops covering
    /// them without anything else to undo.
    /// </summary>
    [Column("covers_from")]
    public DateTime? CoversFrom { get; set; }

    [Column("covers_to")]
    public DateTime? CoversTo { get; set; }

    [Column("sort")]
    public int Sort { get; set; }

    public Invoice Invoice { get; set; } = null!;
}
