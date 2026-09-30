using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// The one place that decides whether blocked time touches a slot. Booking create and move,
/// recurring series, recorded standing weeks, availability and search all ask here, so a
/// block cannot be honoured by one door and ignored by another.
/// </summary>
public static class VenueBlocks
{
    /// <summary>Does this block close this pitch? A block with no pitch closes every pitch.</summary>
    public static bool Covers(VenueBlock block, string? pitchId) =>
        block.PitchId == null || block.PitchId == pitchId;

    /// <summary>The first block that overlaps [date + start, + duration) on this pitch, if any.</summary>
    public static VenueBlock? FirstOverlap(
        IEnumerable<VenueBlock> blocks, string? pitchId, DateTime date, TimeSpan start, int durationMinutes)
    {
        var from = date.Date + start;
        var to = from.AddMinutes(durationMinutes);
        return blocks
            .Where(b => Covers(b, pitchId) && b.StartsAt < to && b.EndsAt > from)
            .OrderBy(b => b.StartsAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// Blocks that touch [from, toExclusive) at these venues. Loaded once, then checked in
    /// memory with <see cref="FirstOverlap"/> or <see cref="MinutesOn"/>.
    /// </summary>
    public static Task<List<VenueBlock>> LoadAsync(
        AppDbContext db, IReadOnlyCollection<string> venueIds, DateTime from, DateTime toExclusive) =>
        db.VenueBlocks.AsNoTracking()
            .Where(b => venueIds.Contains(b.VenueId) && b.StartsAt < toExclusive && b.EndsAt > from)
            .ToListAsync();

    /// <summary>
    /// Blocks for one venue on one operating date. Reaches a day past it, because a late
    /// window (18:00–02:00) sells time after midnight under the date it opened on.
    /// </summary>
    public static Task<List<VenueBlock>> ForDateAsync(AppDbContext db, string venueId, DateTime date) =>
        LoadAsync(db, [venueId], date.Date, date.Date.AddDays(2));

    public static async Task<VenueBlock?> OverlapAsync(
        AppDbContext db, string venueId, string? pitchId, DateTime date, TimeSpan start, int durationMinutes) =>
        FirstOverlap(await ForDateAsync(db, venueId, date), pitchId, date, start, durationMinutes);

    /// <summary>
    /// Where a block falls on an operating date, as minutes from that date's midnight —
    /// clipped to [0, 48h) so it lines up with a late window's minutes past 24:00.
    /// Null when it does not touch the date.
    /// </summary>
    public static (double From, double To)? MinutesOn(VenueBlock block, DateTime date)
    {
        var dayStart = date.Date;
        var from = Math.Max(0, (block.StartsAt - dayStart).TotalMinutes);
        var to = Math.Min(48 * 60, (block.EndsAt - dayStart).TotalMinutes);
        return to > from ? (from, to) : null;
    }

    /// <summary>"Blocked 18:00–22:00 (Maintenance)" — for a 409 the desk can act on.</summary>
    public static string Describe(VenueBlock block, string? pitchName)
    {
        var where = pitchName == null || block.PitchId == null ? "The venue" : pitchName;
        var span = block.StartsAt.Date == block.EndsAt.Date
            ? $"{block.StartsAt:HH:mm}–{block.EndsAt:HH:mm}"
            : $"{block.StartsAt:yyyy-MM-dd HH:mm} – {block.EndsAt:yyyy-MM-dd HH:mm}";
        var why = string.IsNullOrWhiteSpace(block.Reason) ? "" : $" ({block.Reason})";
        return $"{where} is blocked {span}{why}.";
    }
}
