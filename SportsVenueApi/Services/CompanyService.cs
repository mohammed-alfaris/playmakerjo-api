using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services;

/// <summary>
/// Makes sure an owner's company exists, with its starter roles.
///
/// The AddCompanies migration created a company for every owner that existed then. Owners
/// created afterwards — by an admin, by a role change, by a seeder — get theirs here, on first
/// need, so no code path has to remember to create one and none can leave an owner without.
/// </summary>
public sealed class CompanyService
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;

    public CompanyService(AppDbContext db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    /// <summary>
    /// The owner's company, created if missing. A new company takes the platform's CURRENT
    /// default limits — copied, so a later change to the default never moves an existing
    /// company's limit — and the two starter roles.
    /// </summary>
    public async Task<Company> EnsureAsync(string ownerId, CancellationToken ct = default)
    {
        var company = await _db.Companies.FirstOrDefaultAsync(c => c.OwnerId == ownerId, ct);
        if (company != null)
        {
            await EnsureStarterRolesAsync(ownerId, ct);
            return company;
        }

        var owner = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == ownerId, ct)
            ?? throw new InvalidOperationException($"No user '{ownerId}'.");
        var settings = await _settings.GetAsync();

        company = new Company
        {
            OwnerId = ownerId,
            Name = owner.Name.Length > 120 ? owner.Name[..120] : owner.Name,
            MaxVenues = settings.DefaultMaxVenues,
            MaxStaff = settings.DefaultMaxStaff,
        };
        _db.Companies.Add(company);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two requests raced to create the same company; the other one won. Use theirs.
            _db.Entry(company).State = EntityState.Detached;
            company = await _db.Companies.FirstAsync(c => c.OwnerId == ownerId, ct);
        }

        await EnsureStarterRolesAsync(ownerId, ct);
        return company;
    }

    /// <summary>
    /// A company with no roles at all gets the two that match the old read/write levels, so an
    /// owner opening the Team page for the first time has something to assign. A company that
    /// has deliberately deleted them keeps whatever roles it has — this only fills an empty set.
    /// </summary>
    public async Task EnsureStarterRolesAsync(string ownerId, CancellationToken ct = default)
    {
        if (await _db.StaffRoles.AnyAsync(r => r.OwnerId == ownerId, ct)) return;

        _db.StaffRoles.AddRange(
            new StaffRole
            {
                OwnerId = ownerId,
                Name = StaffPermissions.LegacyWriteRoleName,
                Permissions = StaffPermissions.LegacyWrite.ToList(),
            },
            new StaffRole
            {
                OwnerId = ownerId,
                Name = StaffPermissions.LegacyReadRoleName,
                Permissions = StaffPermissions.LegacyRead.ToList(),
            });

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Raced with another request seeding the same company: its roles stand.
            foreach (var e in _db.ChangeTracker.Entries<StaffRole>().Where(e => e.State == EntityState.Added).ToList())
                e.State = EntityState.Detached;
        }
    }

    /// <summary>
    /// The company role standing in for an old read/write level: the starter role with that
    /// name if the owner still has it. Null if they have renamed or removed it.
    /// </summary>
    public async Task<StaffRole?> LegacyRoleAsync(string ownerId, string? level, CancellationToken ct = default)
    {
        await EnsureStarterRolesAsync(ownerId, ct);
        var name = level == "write" ? StaffPermissions.LegacyWriteRoleName : StaffPermissions.LegacyReadRoleName;
        return await _db.StaffRoles.FirstOrDefaultAsync(r => r.OwnerId == ownerId && r.Name == name, ct);
    }
}
