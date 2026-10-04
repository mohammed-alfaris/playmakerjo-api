using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services;

/// <summary>
/// Who the caller is and what they may do, read from the database once per request.
///
/// Before this, every controller re-derived access from JWT claims: the role, the
/// <c>owner_id</c> of a staff member's employer and a two-level <c>permissions</c> string. A
/// claim is a snapshot taken at login or refresh, so a suspension, a permission change or a
/// banned employer took up to one access-token lifetime to bite — and a banned owner's staff
/// were never cut off at all, because nothing ever looked at the owner's status.
///
/// Reading the row per request makes each of those take effect on the very next call, and
/// puts the whole rule — company, role, venue scope — in one place instead of five.
///
/// Loaded by <see cref="AccessContextFilter"/> before any controller action runs, so every
/// member here is synchronous.
/// </summary>
public sealed class AccessContext
{
    private readonly AppDbContext _db;
    private HashSet<string> _permissions = new(StringComparer.Ordinal);
    private HashSet<string>? _restrictedVenueIds;

    public AccessContext(AppDbContext db) => _db = db;

    public string UserId { get; private set; } = "";

    /// <summary>The caller's role as the database has it now — not as the token remembers it.</summary>
    public string Role { get; private set; } = "";

    public bool IsAdmin => Role == "super_admin";
    public bool IsOwner => Role == "venue_owner";
    public bool IsStaff => Role == "venue_staff";
    public bool IsPlayer => Role == "player";

    /// <summary>
    /// The company whose back office the caller works in: the owner's own id for an owner, the
    /// employer's id for a staff member. Null for everyone else — and null for a staff member
    /// who is suspended, unlinked, or whose employer is banned. Null means no back-office access.
    /// </summary>
    public string? CompanyId { get; private set; }

    /// <summary>
    /// The caller's company exists but PlayMaker has suspended it. <see cref="CompanyId"/> is
    /// then null — no back office — and this says why, so the dashboard can explain instead of
    /// showing empty screens.
    /// </summary>
    public bool CompanySuspended { get; private set; }

    public string? StaffRoleId { get; private set; }
    public string? StaffRoleName { get; private set; }

    /// <summary>
    /// For staff limited to some venues, those venue ids. Null means unrestricted within the
    /// company — every venue it has, including ones added later.
    /// </summary>
    public IReadOnlyCollection<string>? RestrictedVenueIds => _restrictedVenueIds;

    /// <summary>The permission keys the caller holds. Owners and admins hold all of them.</summary>
    public IReadOnlyCollection<string> Permissions =>
        IsAdmin || (IsOwner && CompanyId != null) ? StaffPermissions.All : _permissions;

    /// <summary>
    /// Does the caller hold this permission at all? Owners and admins hold every one; staff hold
    /// their role's. Says nothing about WHICH venues — pair it with <see cref="CanSeeVenue"/>.
    /// </summary>
    public bool Has(string permission) =>
        IsAdmin
        || (IsOwner && CompanyId != null)
        || (IsStaff && CompanyId != null && _permissions.Contains(permission));

    /// <summary>Is this venue inside the caller's back office?</summary>
    public bool CanSeeVenue(Venue venue) =>
        IsAdmin
        || (CompanyId != null
            && venue.OwnerId == CompanyId
            && (_restrictedVenueIds == null || _restrictedVenueIds.Contains(venue.Id)));

    /// <summary>May the caller do this on this venue? Both the permission and the venue must fit.</summary>
    public bool Can(string permission, Venue venue) => CanSeeVenue(venue) && Has(permission);

    /// <summary>
    /// Editing, deleting or re-pricing a venue: owner and admin only, whatever a role says.
    /// </summary>
    public bool CanManageVenue(Venue venue) => IsAdmin || (IsOwner && venue.OwnerId == UserId);

    /// <summary>
    /// Narrow a venue query to the caller's back office. Admin: unchanged. No company: nothing.
    /// </summary>
    public IQueryable<Venue> ScopeVenues(IQueryable<Venue> q)
    {
        if (IsAdmin) return q;
        if (CompanyId == null) return q.Where(_ => false);
        var companyId = CompanyId;
        q = q.Where(v => v.OwnerId == companyId);
        if (_restrictedVenueIds != null)
        {
            var ids = _restrictedVenueIds.ToList();
            q = q.Where(v => ids.Contains(v.Id));
        }
        return q;
    }

    /// <summary>The same narrowing, for a query over anything that belongs to a venue.</summary>
    public IQueryable<Booking> ScopeBookings(IQueryable<Booking> q)
    {
        if (IsAdmin) return q;
        if (CompanyId == null) return q.Where(_ => false);
        var companyId = CompanyId;
        q = q.Where(b => b.Venue.OwnerId == companyId);
        if (_restrictedVenueIds != null)
        {
            var ids = _restrictedVenueIds.ToList();
            q = q.Where(b => ids.Contains(b.VenueId));
        }
        return q;
    }

    private Task<bool> SuspendedAsync(string ownerId, CancellationToken ct) =>
        _db.Companies.AsNoTracking().AnyAsync(c => c.OwnerId == ownerId && c.SuspendedAt != null, ct);

    public async Task LoadAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (string.IsNullOrEmpty(id)) return;

        var user = await _db.Users.AsNoTracking()
            .Include(u => u.StaffRole)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        // A token for a row that no longer exists is nobody.
        if (user == null) return;

        UserId = user.Id;
        Role = user.Role;

        if (IsOwner)
        {
            if (await SuspendedAsync(user.Id, ct)) { CompanySuspended = true; return; }
            CompanyId = user.Id;
            return;
        }

        if (!IsStaff) return;

        StaffRoleId = user.StaffRoleId;
        StaffRoleName = user.StaffRole?.Name;

        // A suspended clerk, one linked to nobody, or one whose employer is banned has no back
        // office. Each of these used to keep working until their token expired — the last one
        // indefinitely, since login and refresh only ever checked the clerk's own status.
        if (user.Status != "active" || string.IsNullOrEmpty(user.ManagedByOwnerId)) return;

        var employerActive = await _db.Users.AsNoTracking().AnyAsync(u =>
            u.Id == user.ManagedByOwnerId && u.Role == "venue_owner" && u.Status == "active", ct);
        if (!employerActive) return;
        if (await SuspendedAsync(user.ManagedByOwnerId, ct)) { CompanySuspended = true; return; }

        CompanyId = user.ManagedByOwnerId;

        // A role only counts if it is this company's. A staff row with no role keeps the level
        // it had before roles existed — the backstop if a role is ever removed from under it.
        _permissions = StaffPermissions.For(user, CompanyId);

        if (!user.StaffAllVenues)
            _restrictedVenueIds = new HashSet<string>(user.StaffVenueIds, StringComparer.Ordinal);
    }
}

/// <summary>
/// Loads <see cref="AccessContext"/> for signed-in callers before the action runs. Anonymous
/// callers are left empty, which grants nothing.
/// </summary>
public sealed class AccessContextFilter : IAsyncActionFilter
{
    private readonly AccessContext _access;

    public AccessContextFilter(AccessContext access) => _access = access;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true)
            await _access.LoadAsync(user, context.HttpContext.RequestAborted);
        await next();
    }
}
