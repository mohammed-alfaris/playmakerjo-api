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

    public SettingsController(SettingsService settings, ILogger<SettingsController> logger)
    {
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
        if (req.PlatformFeePercentage is { } fee && (fee < 0 || fee > 100))
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = "platformFeePercentage must be between 0 and 100",
            });

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
        UpdatedAt = row.UpdatedAt.ToString("o"),
    };
}
