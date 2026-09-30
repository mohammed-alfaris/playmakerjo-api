using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// Singleton row (Id == 1) that stores platform-wide configuration editable
/// by super_admin from the dashboard Settings page.
/// </summary>
[Table("platform_settings")]
public class PlatformSettings
{
    [Key]
    [Column("id")]
    public int Id { get; set; } = 1;

    [Column("platform_fee_percentage")]
    public double PlatformFeePercentage { get; set; } = 5.0;

    [Column("maintenance_mode")]
    public bool MaintenanceMode { get; set; } = false;

    [Column("maintenance_message_en", TypeName = "text")]
    public string MaintenanceMessageEn { get; set; } =
        "PlayMaker JO is temporarily unavailable for maintenance. We'll be back shortly.";

    [Column("maintenance_message_ar", TypeName = "text")]
    public string MaintenanceMessageAr { get; set; } =
        "تطبيق PlayMaker JO متوقف مؤقتًا للصيانة. سنعود للعمل قريبًا.";

    /// <summary>
    /// Venue limit given to a company when it is created. Null = unlimited. Copied, not
    /// inherited: changing it later affects only companies created afterwards.
    /// </summary>
    [Column("default_max_venues")]
    public int? DefaultMaxVenues { get; set; }

    /// <summary>Staff limit given to a new company. Null = unlimited. Copied, as above.</summary>
    [Column("default_max_staff")]
    public int? DefaultMaxStaff { get; set; }

    // ── Billing defaults ─────────────────────────────────────────────────────
    // A company without its own price pays these. Changing them moves every such company's
    // NEXT invoice; invoices already drafted keep the prices they were drafted with.

    [Column("price_first_venue")]
    public double PriceFirstVenue { get; set; } = 30;

    [Column("price_extra_venue")]
    public double PriceExtraVenue { get; set; } = 15;

    [Column("setup_fee")]
    public double SetupFee { get; set; } = 100;

    /// <summary>Free days a new company starts with.</summary>
    [Column("trial_days")]
    public int TrialDays { get; set; } = 30;

    /// <summary>Days an issued invoice has before it counts as overdue.</summary>
    [Column("payment_terms_days")]
    public int PaymentTermsDays { get; set; } = 14;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
