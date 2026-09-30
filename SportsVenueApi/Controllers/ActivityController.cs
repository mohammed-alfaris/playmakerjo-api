using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

public class ActivityItem
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("ownerId")] public string? OwnerId { get; set; }
    /// <summary>Filled for the admin, who reads across companies.</summary>
    [JsonPropertyName("companyName")] public string? CompanyName { get; set; }
    [JsonPropertyName("actorName")] public string? ActorName { get; set; }
    [JsonPropertyName("actorRole")] public string? ActorRole { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("entityType")] public string EntityType { get; set; } = "";
    [JsonPropertyName("entityId")] public string? EntityId { get; set; }
    /// <summary>"english|arabic"</summary>
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
}

/// <summary>
/// GET /api/v1/activity — who did what. The owner reads their company's; the admin reads any
/// company's (owner_id), the platform's own changes (owner_id=platform), or everything.
/// Staff do not: the log is how an owner checks on the desk.
/// Filters: from/to (Amman dates, inclusive), area ("booking", "payment", "venue", …), actor_id.
/// </summary>
[ApiController]
[Authorize]
public class ActivityController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AccessContext _access;

    public ActivityController(AppDbContext db, AccessContext access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet("api/v1/activity")]
    public async Task<IActionResult> List(
        [FromQuery(Name = "owner_id")] string? ownerId = null,
        [FromQuery] string? from = null, [FromQuery] string? to = null,
        [FromQuery] string? area = null, [FromQuery(Name = "actor_id")] string? actorId = null,
        [FromQuery] int page = 1, [FromQuery] int limit = 50)
    {
        if (page < 1) page = 1;
        if (limit is < 1 or > 200) limit = 50;

        var q = _db.AuditEvents.AsNoTracking().AsQueryable();
        if (_access.IsAdmin)
        {
            if (ownerId == "platform") q = q.Where(e => e.OwnerId == null);
            else if (!string.IsNullOrEmpty(ownerId)) q = q.Where(e => e.OwnerId == ownerId);
        }
        else if (_access.IsOwner && _access.CompanyId != null)
        {
            var company = _access.CompanyId;
            q = q.Where(e => e.OwnerId == company);
        }
        else return Forbid();

        // Dates are Amman days; stored times are UTC.
        if (DateTime.TryParse(from, out var f)) { var fromUtc = f.Date.AddHours(-PlatformConstants.JordanUtcOffsetHours); q = q.Where(e => e.At >= fromUtc); }
        if (DateTime.TryParse(to, out var t)) { var toUtc = t.Date.AddDays(1).AddHours(-PlatformConstants.JordanUtcOffsetHours); q = q.Where(e => e.At < toUtc); }
        if (!string.IsNullOrEmpty(area)) { var prefix = area + "."; q = q.Where(e => e.Action.StartsWith(prefix)); }
        if (!string.IsNullOrEmpty(actorId)) q = q.Where(e => e.ActorUserId == actorId);

        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(e => e.At).ThenByDescending(e => e.Id)
            .Skip((page - 1) * limit).Take(limit).ToListAsync();

        Dictionary<string, string> names = [];
        if (_access.IsAdmin)
        {
            var ids = rows.Where(r => r.OwnerId != null).Select(r => r.OwnerId!).Distinct().ToList();
            names = await _db.Companies.AsNoTracking().Where(c => ids.Contains(c.OwnerId)).ToDictionaryAsync(c => c.OwnerId, c => c.Name);
        }

        return Ok(new ApiResponse<List<ActivityItem>>
        {
            Data = rows.Select(e => new ActivityItem
            {
                Id = e.Id, At = e.At, OwnerId = e.OwnerId,
                CompanyName = e.OwnerId != null && names.TryGetValue(e.OwnerId, out var n) ? n : null,
                ActorName = e.ActorName, ActorRole = e.ActorRole, Action = e.Action,
                EntityType = e.EntityType, EntityId = e.EntityId, Summary = e.Summary,
            }).ToList(),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
        });
    }
}
