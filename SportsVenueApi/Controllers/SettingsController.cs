using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Settings;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

/// <summary>
/// Platform-wide settings. Two entry points:
///
/// * <c>GET /api/v1/platform/status</c> — public, no auth. Used by the mobile
///   app to decide whether to show the maintenance screen. Returns the
///   minimum fields needed for that decision.
///
/// * <c>GET /api/v1/settings</c> and <c>PATCH /api/v1/settings</c> —
///   super_admin only. Full read/write of the singleton settings row.
/// </summary>
[ApiController]
public class SettingsController : ControllerBase
{
    private readonly SettingsService _settings;
    private readonly ILogger<SettingsController> _logger;

    private readonly AuditLog _audit;

    public SettingsController(SettingsService settings, ILogger<SettingsController> logger, AuditLog audit)
    {
        _audit = audit;
        _settings = settings;
        _logger = logger;
    }

    // GET /api/v1/platform/status — public
    [HttpGet("api/v1/platform/status")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublicStatus(CancellationToken ct)
    {
        var row = await _settings.GetAsync(ct);
        var payload = new PlatformStatusResponse
        {
            MaintenanceMode = row.MaintenanceMode,
            MaintenanceMessageEn = row.MaintenanceMessageEn,
            MaintenanceMessageAr = row.MaintenanceMessageAr,
        };
        return Ok(new ApiResponse<PlatformStatusResponse> { Data = payload });
    }

    // GET /api/v1/settings — super_admin
    [HttpGet("api/v1/settings")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var row = await _settings.GetAsync(ct);
        return Ok(new ApiResponse<SettingsResponse> { Data = ToResponse(row) });
    }

    // PATCH /api/v1/settings — super_admin
    [HttpPatch("api/v1/settings")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest req, CancellationToken ct)
    {
        if (req.DefaultLimits is { } limits && (limits.MaxVenues < 0 || limits.MaxStaff < 0))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Limits cannot be negative." });

        if (req.Billing is { } b && (b.PriceFirstVenue < 0 || b.PriceExtraVenue < 0 || b.SetupFee < 0
                || b.TrialDays is < 0 or > 365 || b.PaymentTermsDays is < 0 or > 120))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Billing defaults are out of range." });

        if (req.PlatformFeePercentage is { } fee && (fee < 0 || fee > 100))
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "platformFeePercentage must be between 0 and 100",
            });

        // Staged on the same scoped context, so it commits with the settings row itself.
        var parts = new List<(string En, string Ar)>();
        if (req.PlatformFeePercentage is { } newFee) parts.Add(($"commission {newFee}%", $"العمولة {newFee}%"));
        if (req.MaintenanceMode is { } mm) parts.Add((mm ? "maintenance on" : "maintenance off", mm ? "تفعيل الصيانة" : "إيقاف الصيانة"));
        if (req.DefaultLimits != null) parts.Add(("default limits", "الحدود الافتراضية"));
        if (req.Billing is { } bd) parts.Add(($"prices {bd.PriceFirstVenue} + {bd.PriceExtraVenue} JOD, setup {bd.SetupFee} JOD, trial {bd.TrialDays} days",
            $"الأسعار {bd.PriceFirstVenue} + {bd.PriceExtraVenue} د.أ، التأسيس {bd.SetupFee} د.أ، التجربة {bd.TrialDays} يوم"));
        if (parts.Count > 0)
            await _audit.AddAsync("settings.updated", null, "settings", "1",
                $"Platform settings: {string.Join(", ", parts.Select(p => p.En))}",
                $"إعدادات المنصة: {string.Join("، ", parts.Select(p => p.Ar))}");

        var updated = await _settings.UpdateAsync(row =>
        {
            if (req.PlatformFeePercentage.HasValue)
                row.PlatformFeePercentage = req.PlatformFeePercentage.Value;
            if (req.MaintenanceMode.HasValue)
                row.MaintenanceMode = req.MaintenanceMode.Value;
            if (req.MaintenanceMessageEn != null)
                row.MaintenanceMessageEn = req.MaintenanceMessageEn;
            if (req.MaintenanceMessageAr != null)
                row.MaintenanceMessageAr = req.MaintenanceMessageAr;
            if (req.Billing != null)
            {
                row.PriceFirstVenue = req.Billing.PriceFirstVenue;
                row.PriceExtraVenue = req.Billing.PriceExtraVenue;
                row.SetupFee = req.Billing.SetupFee;
                row.TrialDays = req.Billing.TrialDays;
                row.PaymentTermsDays = req.Billing.PaymentTermsDays;
            }
            if (req.DefaultLimits != null)
            {
                row.DefaultMaxVenues = req.DefaultLimits.MaxVenues;
                row.DefaultMaxStaff = req.DefaultLimits.MaxStaff;
            }
        }, ct);

        _logger.LogInformation(
            "Platform settings updated: fee={Fee}%, maintenance={Maintenance}",
            updated.PlatformFeePercentage, updated.MaintenanceMode);

        return Ok(new ApiResponse<SettingsResponse>
        {
            Data = ToResponse(updated),
            Message = "Settings updated",
        });
    }

    private static SettingsResponse ToResponse(Models.PlatformSettings row) => new()
    {
        PlatformFeePercentage = row.PlatformFeePercentage,
        MaintenanceMode = row.MaintenanceMode,
        MaintenanceMessageEn = row.MaintenanceMessageEn,
        MaintenanceMessageAr = row.MaintenanceMessageAr,
        DefaultMaxVenues = row.DefaultMaxVenues,
        DefaultMaxStaff = row.DefaultMaxStaff,
        Billing = new BillingDefaults
        {
            PriceFirstVenue = row.PriceFirstVenue,
            PriceExtraVenue = row.PriceExtraVenue,
            SetupFee = row.SetupFee,
            TrialDays = row.TrialDays,
            PaymentTermsDays = row.PaymentTermsDays,
        },
        UpdatedAt = row.UpdatedAt.ToString("o"),
    };
}
