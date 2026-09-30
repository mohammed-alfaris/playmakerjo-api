using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Staff;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

/// <summary>
/// An owner's own staff roles. Each role is a name plus a set of permission keys, and belongs
/// to one company — owners never see or touch another company's roles.
///
/// Managing roles is owner-only, whatever a role grants: a permission that let a clerk edit
/// roles would let them grant themselves anything. An admin may manage a company's roles by
/// naming it with owner_id.
/// </summary>
[ApiController]
[Authorize]
public class StaffRolesController : ControllerBase
{
    private const int MaxNameLength = 60;

    private readonly AppDbContext _db;
    private readonly AccessContext _access;
    private readonly CompanyService _companies;
    private readonly AuditLog _audit;

    public StaffRolesController(AppDbContext db, AccessContext access, CompanyService companies, AuditLog audit)
    {
        _db = db;
        _access = access;
        _companies = companies;
        _audit = audit;
    }

    /// <summary>Whose roles: the owner's own; an admin must name a company. Null = forbidden.</summary>
    private string? ResolveCompany(string? ownerId) =>
        _access.IsOwner ? _access.CompanyId
        : _access.IsAdmin && !string.IsNullOrWhiteSpace(ownerId) ? ownerId
        : null;

    /// <summary>GET /api/v1/staff-permissions — every grantable permission, grouped for display.</summary>
    [HttpGet("api/v1/staff-permissions")]
    public IActionResult Permissions() =>
        Ok(new ApiResponse<List<StaffPermissionInfo>>
        {
            Data = StaffPermissions.All
                .Select(k => new StaffPermissionInfo { Key = k, Group = k.Split('.')[0] })
                .ToList()
        });

    [HttpGet("api/v1/staff-roles")]
    public async Task<IActionResult> List([FromQuery] string? owner_id = null)
    {
        var companyId = ResolveCompany(owner_id);
        if (companyId == null) return Forbid();
        await _companies.EnsureStarterRolesAsync(companyId);

        var roles = await _db.StaffRoles.AsNoTracking()
            .Where(r => r.OwnerId == companyId)
            .OrderBy(r => r.Name)
            .ToListAsync();
        var counts = await StaffCountsAsync(companyId);

        return Ok(new ApiResponse<List<StaffRoleResponse>>
        {
            Data = roles.Select(r => ToDto(r, counts.GetValueOrDefault(r.Id))).ToList()
        });
    }

    [HttpPost("api/v1/staff-roles")]
    public async Task<IActionResult> Create([FromBody] StaffRoleRequest req, [FromQuery] string? owner_id = null)
    {
        var companyId = ResolveCompany(owner_id);
        if (companyId == null) return Forbid();

        // Seed the starter roles first, or an owner whose first action is creating a role named
        // "Front desk" gets a duplicate the moment the starters are seeded afterwards.
        await _companies.EnsureStarterRolesAsync(companyId);

        var name = (req.Name ?? "").Trim();
        var invalid = ValidateName(name) ?? ValidatePermissions(req.Permissions);
        if (invalid != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = invalid });

        if (await _db.StaffRoles.AnyAsync(r => r.OwnerId == companyId && r.Name == name))
            return Conflict(new ApiResponse<object> { Success = false, Message = "You already have a role with that name." });

        var role = new StaffRole
        {
            OwnerId = companyId,
            Name = name,
            Permissions = Normalize(req.Permissions!),
        };
        _db.StaffRoles.Add(role);
        await _audit.AddAsync("role.created", companyId, "role", role.Id, $"Created the role {role.Name}", $"إنشاء الدور {role.Name}");
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<StaffRoleResponse> { Data = ToDto(role, 0), Message = "Role created" });
    }

    [HttpPatch("api/v1/staff-roles/{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] StaffRoleRequest req, [FromQuery] string? owner_id = null)
    {
        var companyId = ResolveCompany(owner_id);
        if (companyId == null) return Forbid();

        // Another company's role is reported as not found, not forbidden: it is not theirs to
        // learn exists.
        var role = await _db.StaffRoles.FirstOrDefaultAsync(r => r.Id == id && r.OwnerId == companyId);
        if (role == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Role not found" });

        if (req.Name != null)
        {
            var name = req.Name.Trim();
            var invalidName = ValidateName(name);
            if (invalidName != null)
                return BadRequest(new ApiResponse<object> { Success = false, Message = invalidName });
            if (await _db.StaffRoles.AnyAsync(r => r.OwnerId == companyId && r.Name == name && r.Id != id))
                return Conflict(new ApiResponse<object> { Success = false, Message = "You already have a role with that name." });
            role.Name = name;
        }

        if (req.Permissions != null)
        {
            var invalid = ValidatePermissions(req.Permissions);
            if (invalid != null)
                return BadRequest(new ApiResponse<object> { Success = false, Message = invalid });
            role.Permissions = Normalize(req.Permissions);
        }

        role.UpdatedAt = DateTime.UtcNow;
        await _audit.AddAsync("role.updated", companyId, "role", role.Id,
            $"Changed the role {role.Name}: {string.Join(", ", role.Permissions)}", $"تعديل الدور {role.Name}: {string.Join("، ", role.Permissions)}");
        await _db.SaveChangesAsync();

        var count = (await StaffCountsAsync(companyId)).GetValueOrDefault(role.Id);
        return Ok(new ApiResponse<StaffRoleResponse> { Data = ToDto(role, count), Message = "Role updated" });
    }

    /// <summary>
    /// Refused while anyone holds the role: deleting it would silently drop those people back to
    /// their pre-roles level, which is not something an owner would guess from a delete button.
    /// </summary>
    [HttpDelete("api/v1/staff-roles/{id}")]
    public async Task<IActionResult> Delete(string id, [FromQuery] string? owner_id = null)
    {
        var companyId = ResolveCompany(owner_id);
        if (companyId == null) return Forbid();

        var role = await _db.StaffRoles.FirstOrDefaultAsync(r => r.Id == id && r.OwnerId == companyId);
        if (role == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Role not found" });

        var inUse = (await StaffCountsAsync(companyId)).GetValueOrDefault(role.Id);
        if (inUse > 0)
            return Conflict(new ApiResponse<object>
            {
                Success = false,
                Message = $"{inUse} staff member(s) have this role. Give them another role first."
            });

        _db.StaffRoles.Remove(role);
        await _audit.AddAsync("role.deleted", companyId, "role", role.Id, $"Deleted the role {role.Name}", $"حذف الدور {role.Name}");
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Message = "Role deleted" });
    }

    // ------------------------------------------------------------------------------------

    private static string? ValidateName(string name) =>
        name.Length == 0 ? "A role needs a name."
        : name.Length > MaxNameLength ? $"Role names can be at most {MaxNameLength} characters."
        : null;

    private static string? ValidatePermissions(List<string>? permissions)
    {
        if (permissions == null) return "Choose what this role can do.";
        var unknown = permissions.FirstOrDefault(p => !StaffPermissions.IsValid(p));
        return unknown == null ? null : $"Unknown permission '{unknown}'.";
    }

    /// <summary>
    /// Deduplicated, in catalog order. A "manage" permission implies its "view": a role that may
    /// edit customers but not see them would be a role nobody could actually use.
    /// </summary>
    private static List<string> Normalize(List<string> permissions)
    {
        var set = new HashSet<string>(permissions, StringComparer.Ordinal);
        foreach (var p in permissions)
        {
            var group = p.Split('.')[0];
            if (!p.EndsWith(".view") && StaffPermissions.IsValid($"{group}.view"))
                set.Add($"{group}.view");
        }
        return StaffPermissions.All.Where(set.Contains).ToList();
    }

    private async Task<Dictionary<string, int>> StaffCountsAsync(string companyId) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.Role == "venue_staff" && u.ManagedByOwnerId == companyId && u.StaffRoleId != null)
            .GroupBy(u => u.StaffRoleId!)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);

    private static StaffRoleResponse ToDto(StaffRole r, int staffCount) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Permissions = r.Permissions,
        StaffCount = staffCount,
    };
}
