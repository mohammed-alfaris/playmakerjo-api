using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services;

/// <summary>
/// Writes the activity log. <see cref="Add"/> only stages the row: the caller's own
/// SaveChanges commits it together with the change it describes, so the log can never claim
/// something happened that did not, or miss something that did.
/// </summary>
public sealed class AuditLog
{
    private readonly AppDbContext _db;
    private readonly AccessContext _access;
    private string? _actorName;
    private bool _actorLoaded;

    public AuditLog(AppDbContext db, AccessContext access)
    {
        _db = db;
        _access = access;
    }

    /// <summary>Money as the log prints it: "20 JOD", "12.5 JOD".</summary>
    public static string Jod(double amount) => amount.ToString("0.###", CultureInfo.InvariantCulture) + " JOD";

    public async Task AddAsync(string action, string? ownerId, string entityType, string? entityId, string en, string ar)
    {
        if (!_actorLoaded)
        {
            _actorLoaded = true;
            if (!string.IsNullOrEmpty(_access.UserId))
                _actorName = await _db.Users.AsNoTracking().Where(u => u.Id == _access.UserId).Select(u => u.Name).FirstOrDefaultAsync();
        }

        _db.AuditEvents.Add(new AuditEvent
        {
            OwnerId = ownerId,
            ActorUserId = string.IsNullOrEmpty(_access.UserId) ? null : _access.UserId,
            ActorName = _actorName,
            ActorRole = string.IsNullOrEmpty(_access.Role) ? null : _access.Role,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = $"{en}|{ar}",
        });
    }

    /// <summary>A booking as the log names it: "Khalid · 2026-10-03 18:00 · Court 1".</summary>
    public static string Describe(Booking b)
    {
        var who = b.Customer?.Name ?? (b.IsManual ? null : b.Player?.Name);
        var when = $"{b.Date:yyyy-MM-dd} {b.StartTime}";
        return who == null ? when : $"{who} · {when}";
    }
}
