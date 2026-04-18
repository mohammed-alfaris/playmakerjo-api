using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services;

/// <summary>
/// Read/write access to the singleton <see cref="PlatformSettings"/> row.
///
/// Callers should treat this as the single source of truth for the platform
/// fee percentage and maintenance-mode flag at runtime. The returned instance
/// is cached in-process for <see cref="CacheTtl"/>; cache is invalidated
/// automatically after any update.
/// </summary>
public class SettingsService
{
    private static PlatformSettings? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private readonly AppDbContext _db;

    public SettingsService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Loads settings, using the in-process cache when fresh.</summary>
    public async Task<PlatformSettings> GetAsync(CancellationToken ct = default)
    {
        if (_cached != null && DateTime.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cached != null && DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            var row = await _db.PlatformSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct);

            if (row == null)
            {
                row = new PlatformSettings { Id = 1 };
                _db.PlatformSettings.Add(row);
                await _db.SaveChangesAsync(ct);
            }

            _cached = row;
            _cachedAt = DateTime.UtcNow;
            return row;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Apply a partial update. The delegate receives the tracked entity and
    /// should mutate only the fields that are changing. Invalidates cache.
    /// </summary>
    public async Task<PlatformSettings> UpdateAsync(Action<PlatformSettings> apply, CancellationToken ct = default)
    {
        var row = await _db.PlatformSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
        var isNew = row == null;
        row ??= new PlatformSettings { Id = 1 };

        apply(row);
        row.UpdatedAt = DateTime.UtcNow;

        if (isNew) _db.PlatformSettings.Add(row);
        await _db.SaveChangesAsync(ct);

        Invalidate();
        return row;
    }

    public void Invalidate()
    {
        _cached = null;
        _cachedAt = DateTime.MinValue;
    }

    /// <summary>Convenience accessor used by the booking controller.</summary>
    public async Task<double> GetPlatformFeePercentageAsync(CancellationToken ct = default)
        => (await GetAsync(ct)).PlatformFeePercentage;
}
