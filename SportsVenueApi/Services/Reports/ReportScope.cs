using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;

namespace SportsVenueApi.Services.Reports;

/// <summary>
/// What one request may report on. <see cref="VenueIds"/> null means platform-wide, which
/// only an admin with no owner filter ever gets.
/// </summary>
/// <param name="OwnerId">The company being reported on; null when platform-wide.</param>
/// <param name="CanSeeCustomers">Customer names and phones. Staff need customers.view on top of reports.view.</param>
/// <param name="CanSeeTeam">Who recorded which money. Owner and admin only — a clerk auditing the other clerks' drawers is not a clerk's job.</param>
public sealed record ReportScope(
    bool Allowed,
    List<string>? VenueIds,
    string? OwnerId,
    bool IsAdmin,
    bool CanSeeCustomers,
    bool CanSeeTeam)
{
    public bool PlatformWide => VenueIds == null;

    public static readonly ReportScope Denied = new(false, null, null, false, false, false);
}

/// <summary>
/// Resolves <see cref="ReportScope"/> from the per-request <see cref="AccessContext"/>.
///
/// Deny by default: a role not handled here gets nothing. The owner_id query parameter is
/// honoured for admins only — an owner or clerk is always pinned to their own company, so
/// asking for a competitor's id yields their own numbers, never the competitor's.
/// </summary>
public class ReportScopeResolver
{
    private readonly AppDbContext _db;
    private readonly AccessContext _access;

    public ReportScopeResolver(AppDbContext db, AccessContext access)
    {
        _db = db;
        _access = access;
    }

    public async Task<ReportScope> ResolveAsync(string? requestedOwnerId, string? venueId = null)
    {
        var scope = await ResolveCompanyAsync(requestedOwnerId);
        if (!scope.Allowed || string.IsNullOrEmpty(venueId)) return scope;

        // A venue filter narrows within the scope; it can never widen it. A venue outside it
        // yields an empty scope, not an error that would confirm the venue exists.
        var narrowed = scope.PlatformWide
            ? await _db.Venues.Where(v => v.Id == venueId).Select(v => v.Id).ToListAsync()
            : scope.VenueIds!.Where(id => id == venueId).ToList();
        return scope with { VenueIds = narrowed };
    }

    private async Task<ReportScope> ResolveCompanyAsync(string? requestedOwnerId)
    {
        if (_access.IsAdmin)
        {
            if (string.IsNullOrEmpty(requestedOwnerId))
                return new ReportScope(true, null, null, true, true, true);

            var ids = await _db.Venues.Where(v => v.OwnerId == requestedOwnerId).Select(v => v.Id).ToListAsync();
            return new ReportScope(true, ids, requestedOwnerId, true, true, true);
        }

        if (_access.IsOwner && _access.CompanyId != null)
        {
            var ids = await _db.Venues.Where(v => v.OwnerId == _access.CompanyId).Select(v => v.Id).ToListAsync();
            return new ReportScope(true, ids, _access.CompanyId, false, true, true);
        }

        // Staff see reports only when their role grants it, and only for the venues they
        // work at — a branch manager's report is that branch's numbers.
        if (_access.IsStaff && _access.Has(StaffPermissions.ReportsView))
        {
            var ids = await _access.ScopeVenues(_db.Venues).Select(v => v.Id).ToListAsync();
            return new ReportScope(true, ids, _access.CompanyId, false,
                CanSeeCustomers: _access.Has(StaffPermissions.CustomersView), CanSeeTeam: false);
        }

        return ReportScope.Denied;
    }
}
