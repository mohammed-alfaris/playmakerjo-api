using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// A venue owner's business, as the CRM sees it: its name and the limits the platform sets on it.
///
/// Keyed on the owner's user id, 1:1. The owner remains the tenant everywhere else —
/// Venue.OwnerId, Customer.OwnerId and User.ManagedByOwnerId all still point at that user —
/// so this table adds to the model without rewriting a single existing key.
/// </summary>
[Table("companies")]
public class Company
{
    [Key]
    [Column("owner_id")]
    [MaxLength(32)]
    public string OwnerId { get; set; } = "";

    [Column("name")]
    [MaxLength(120)]
    public string Name { get; set; } = "";

    [Column("name_ar")]
    [MaxLength(120)]
    public string? NameAr { get; set; }

    /// <summary>How many venues the company may have. Null means no limit.</summary>
    [Column("max_venues")]
    public int? MaxVenues { get; set; }

    /// <summary>How many ACTIVE staff the company may have. Null means no limit.</summary>
    [Column("max_staff")]
    public int? MaxStaff { get; set; }

    // ── Billing ─────────────────────────────────────────────────────────────
    //
    // What PlayMaker charges this company. Invoices are drafted from these on demand (the
    // admin presses Generate for a month) — nothing here charges anyone by itself.

    /// <summary>"monthly" or "annual" (twelve months for the price of ten, no setup fee).</summary>
    [Column("billing_cycle")]
    [MaxLength(10)]
    public string BillingCycle { get; set; } = "monthly";

    /// <summary>
    /// Last free day (Amman calendar date). A month is billed only once the trial ended before
    /// it began — a trial ending mid-month leaves the rest of that month free too.
    /// </summary>
    [Column("trial_ends_on")]
    public DateTime? TrialEndsOn { get; set; }

    /// <summary>This company's price for its first venue. Null = the platform default.</summary>
    [Column("price_first_venue")]
    public double? PriceFirstVenue { get; set; }

    /// <summary>This company's price per additional venue. Null = the platform default.</summary>
    [Column("price_extra_venue")]
    public double? PriceExtraVenue { get; set; }

    /// <summary>This company's price for a small venue. Null = the platform default.</summary>
    [Column("price_small_venue")]
    public double? PriceSmallVenue { get; set; }

    /// <summary>This company's price for a large venue. Null = the platform default.</summary>
    [Column("price_large_venue")]
    public double? PriceLargeVenue { get; set; }

    /// <summary>No setup fee for this company (it is never charged on an annual plan either).</summary>
    [Column("setup_fee_waived")]
    public bool SetupFeeWaived { get; set; }

    /// <summary>
    /// Set by an admin to stop the company working: its owner and staff lose the back office
    /// (they can still sign in and see their invoices) and its venues leave the app. Null = working.
    /// </summary>
    [Column("suspended_at")]
    public DateTime? SuspendedAt { get; set; }

    [Column("suspended_reason")]
    [MaxLength(255)]
    public string? SuspendedReason { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OwnerId))]
    public User Owner { get; set; } = null!;
}
