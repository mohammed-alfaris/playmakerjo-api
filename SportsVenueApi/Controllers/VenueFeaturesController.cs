using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.VenueFeatures;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;

namespace SportsVenueApi.Controllers;

/// <summary>
/// The catalog of venue features: curated by super_admin, read by everyone.
///
/// Reads are anonymous on purpose. The app builds its filter from this list before anyone has
/// signed in, and nothing here is sensitive — names and icons.
/// </summary>
[ApiController]
[Route("api/v1/venue-features")]
[Authorize]
public class VenueFeaturesController : ControllerBase
{
    private const int MaxNameLength = 60;

    private readonly AppDbContext _db;

    public VenueFeaturesController(AppDbContext db) => _db = db;

    private bool IsAdmin => User.FindFirstValue(ClaimTypes.Role) == "super_admin";

    /// <summary>
    /// GET /api/v1/venue-features — active features in display order. An admin may add
    /// <c>includeInactive=true</c> to manage retired ones, and always gets usage counts.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool includeInactive = false)
    {
        var query = _db.VenueFeatures.AsNoTracking();
        if (!(IsAdmin && includeInactive))
            query = query.Where(f => f.IsActive);

        var features = await query
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.NameEn)
            .ToListAsync();

        var counts = IsAdmin ? await UsageCountsAsync() : null;

        var data = features.Select(f =>
        {
            var dto = ToDto(f);
            if (counts != null) dto.VenueCount = counts.GetValueOrDefault(f.Id);
            return dto;
        }).ToList();

        return Ok(new ApiResponse<List<VenueFeatureResponse>> { Data = data });
    }

    [HttpPost]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Create([FromBody] CreateVenueFeatureRequest req)
    {
        var name = VenueFeatureRules.CleanLabel(req.Name);
        var nameAr = VenueFeatureRules.CleanLabel(req.NameAr);

        var invalid = ValidateFields(name, nameAr, req.Icon);
        if (invalid != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = invalid });

        if (await NameTakenAsync(name, nameAr, exceptId: null))
            return Conflict(new ApiResponse<object> { Success = false, Message = "A feature with that name already exists." });

        var baseSlug = VenueFeatureRules.Slugify(name);
        if (baseSlug.Length == 0) baseSlug = "vf-" + Guid.NewGuid().ToString("N")[..8];
        var id = baseSlug;
        for (var n = 2; await _db.VenueFeatures.AnyAsync(f => f.Id == id); n++)
            id = $"{baseSlug}-{n}";

        var sortOrder = req.SortOrder
            ?? ((await _db.VenueFeatures.MaxAsync(f => (int?)f.SortOrder)) ?? 0) + 10;

        var feature = new VenueFeature
        {
            Id = id,
            NameEn = name,
            NameAr = nameAr,
            Icon = req.Icon!,
            SortOrder = sortOrder,
        };

        _db.VenueFeatures.Add(feature);
        if (!await TrySaveAsync())
            return Conflict(new ApiResponse<object> { Success = false, Message = "A feature with that name already exists." });

        return Ok(new ApiResponse<VenueFeatureResponse> { Data = ToDto(feature, venueCount: 0), Message = "Feature created" });
    }

    [HttpPatch("{id}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateVenueFeatureRequest req)
    {
        var feature = await _db.VenueFeatures.FindAsync(id);
        if (feature == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Feature not found" });

        var name = req.Name == null ? feature.NameEn : VenueFeatureRules.CleanLabel(req.Name);
        var nameAr = req.NameAr == null ? feature.NameAr : VenueFeatureRules.CleanLabel(req.NameAr);
        var icon = req.Icon ?? feature.Icon;

        var invalid = ValidateFields(name, nameAr, icon);
        if (invalid != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = invalid });

        if (await NameTakenAsync(name, nameAr, exceptId: feature.Id))
            return Conflict(new ApiResponse<object> { Success = false, Message = "A feature with that name already exists." });

        // The id is deliberately untouched: venues store it, so a rename must not orphan them.
        feature.NameEn = name;
        feature.NameAr = nameAr;
        feature.Icon = icon;
        if (req.SortOrder.HasValue) feature.SortOrder = req.SortOrder.Value;
        if (req.IsActive.HasValue) feature.IsActive = req.IsActive.Value;
        feature.UpdatedAt = DateTime.UtcNow;

        if (!await TrySaveAsync())
            return Conflict(new ApiResponse<object> { Success = false, Message = "A feature with that name already exists." });

        var count = (await UsageCountsAsync()).GetValueOrDefault(feature.Id);
        return Ok(new ApiResponse<VenueFeatureResponse> { Data = ToDto(feature, count), Message = "Feature updated" });
    }

    /// <summary>
    /// DELETE /api/v1/venue-features/{id} — only for a feature no venue uses. Deleting one in use
    /// would silently strip it from every venue that chose it; retiring it (isActive=false) keeps
    /// those venues truthful while taking it off the menu for everyone else.
    /// </summary>
    [HttpDelete("{id}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Delete(string id)
    {
        var feature = await _db.VenueFeatures.FindAsync(id);
        if (feature == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Feature not found" });

        var inUse = (await UsageCountsAsync()).GetValueOrDefault(feature.Id);
        if (inUse > 0)
            return Conflict(new ApiResponse<object>
            {
                Success = false,
                Message = $"Used by {inUse} venue(s). Deactivate it instead, so those venues keep it.",
            });

        _db.VenueFeatures.Remove(feature);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Message = "Feature deleted" });
    }

    // ------------------------------------------------------------------------------------

    private static string? ValidateFields(string name, string nameAr, string? icon)
    {
        if (name.Length == 0 || nameAr.Length == 0)
            return "Both the English and the Arabic name are required.";
        if (name.Length > MaxNameLength || nameAr.Length > MaxNameLength)
            return $"Names must be at most {MaxNameLength} characters.";
        if (!VenueFeatureIcons.IsValid(icon))
            return $"Unknown icon '{icon}'.";
        return null;
    }

    /// <summary>
    /// Compared in SQL, so MySQL's case-insensitive collation decides: "Parking" and "parking"
    /// are the same name. The unique indexes are the backstop for a race between two admins.
    /// </summary>
    private Task<bool> NameTakenAsync(string name, string nameAr, string? exceptId) =>
        _db.VenueFeatures.AnyAsync(f =>
            (exceptId == null || f.Id != exceptId) && (f.NameEn == name || f.NameAr == nameAr));

    private async Task<bool> TrySaveAsync()
    {
        try
        {
            await _db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Venue counts per feature. One read of a single column rather than a query per feature:
    /// the ids live in a JSON list on each venue, not in a join table.
    /// </summary>
    private async Task<Dictionary<string, int>> UsageCountsAsync()
    {
        var lists = await _db.Venues.AsNoTracking().Select(v => v.FeatureIdsJson).ToListAsync();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var json in lists)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            foreach (var id in System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? [])
                counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        return counts;
    }

    private static VenueFeatureResponse ToDto(VenueFeature f, int? venueCount = null) => new()
    {
        Id = f.Id,
        Name = f.NameEn,
        NameAr = f.NameAr,
        Icon = f.Icon,
        SortOrder = f.SortOrder,
        IsActive = f.IsActive,
        VenueCount = venueCount,
    };
}
