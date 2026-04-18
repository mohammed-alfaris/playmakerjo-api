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

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
