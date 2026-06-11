using System.Text.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// Shared availability primitives: operating-hours resolution, pitch matching
/// for bookings/permanents, and the slot-capacity check used by the public
/// availability search.
/// </summary>
public static class AvailabilityHelper
{
    public static OperatingHoursInfo? ResolveHoursForDay(Dictionary<string, object>? hoursMap, string dayName)
    {
        if (hoursMap == null) return null;

        // The dashboard writes keys as full day names ("monday", "tuesday", ...)
        // while older seed data used 3-letter abbreviations ("mon", "tue", ...).
        // Accept both so legacy venues and newly-edited ones keep working.
        var dayFull = dayName.ToLower();
        var dayShort = dayFull.Length >= 3 ? dayFull[..3] : dayFull;
        if (!hoursMap.TryGetValue(dayFull, out var dayHoursObj)
            && !hoursMap.TryGetValue(dayShort, out dayHoursObj))
            return null;

        var dayHoursJson = JsonSerializer.Serialize(dayHoursObj);
        var dayHours = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dayHoursJson);
        if (dayHours == null) return null;

        // Honour the "closed: true" flag written by the dashboard editor — if
        // the day is marked closed there is no open/close window for it.
        if (dayHours.TryGetValue("closed", out var closedEl)
            && closedEl.ValueKind == JsonValueKind.True)
            return null;

        string GetStr(string k, string fallback)
            => dayHours.TryGetValue(k, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;

        return new OperatingHoursInfo
        {
            Open = GetStr("open", "08:00"),
            Close = GetStr("close", "22:00")
        };
    }

    public static OperatingHoursInfo? ResolvePitchHours(PitchDto pitch, string dayName, OperatingHoursInfo? fallback)
    {
        if (pitch.OperatingHours == null) return fallback;
        try
        {
            var json = JsonSerializer.Serialize(pitch.OperatingHours);
            var map = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (map == null) return fallback;
            return ResolveHoursForDay(map, dayName) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// A booking belongs to a pitch when the explicit pitch_id matches OR — on
    /// legacy rows where pitch_id is null — when this pitch is the first pitch
    /// of the booking's sport on the resolved pitch list. This makes legacy
    /// bookings appear on exactly one timeline (never duplicated), and makes
    /// venues with empty <c>pitches</c> (implicit single-pitch) behave exactly
    /// as today.
    /// </summary>
    public static bool MatchesPitch(Booking b, Venue v, PitchDto pitch)
    {
        if (!string.IsNullOrEmpty(b.PitchId))
            return b.PitchId == pitch.Id;
        var firstOfSport = PitchSizes.ResolvedPitches(v)
            .FirstOrDefault(p => string.Equals(p.Sport, b.Sport, StringComparison.OrdinalIgnoreCase));
        return firstOfSport != null && firstOfSport.Id == pitch.Id;
    }

    /// <summary>
    /// Permanent bookings carry an explicit pitch_id when the venue has multiple
    /// pitches. When pitch_id is null we treat the permanent as belonging to the
    /// pitch that matches its size's sport — i.e. the only pitch on a single-pitch
    /// venue (legacy data).
    /// </summary>
    public static bool MatchesPitch(PermanentBooking p, Venue v, PitchDto pitch)
    {
        if (!string.IsNullOrEmpty(p.PitchId))
            return p.PitchId == pitch.Id;
        var resolved = PitchSizes.ResolvedPitches(v);
        return resolved.Count == 1 && resolved[0].Id == pitch.Id;
    }

    /// <summary>
    /// Can this pitch take a booking covering [start, start+duration) on the
    /// given day? Mirrors the collision rules of BookingsController.Create:
    /// the slot must sit inside the resolved operating hours (closed day = no),
    /// and either no overlap exists (non-subdividable) or enough capacity units
    /// remain for the smallest offered size (subdividable).
    /// </summary>
    public static bool PitchHasCapacity(
        Venue venue, PitchDto pitch, TimeSpan start, int duration, string dayName,
        List<Booking> dayBookings, List<PermanentBooking> dayPermanents)
    {
        var venueHours = ResolveHoursForDay(venue.OperatingHours, dayName);
        var hours = ResolvePitchHours(pitch, dayName, venueHours);
        var end = start + TimeSpan.FromMinutes(duration);

        if (hours != null)
        {
            if (!TimeSpan.TryParse(hours.Open, out var open) || !TimeSpan.TryParse(hours.Close, out var close))
                return false;
            if (start < open || end > close)
                return false;
        }
        else if (venue.OperatingHours is { Count: > 0 } || pitch.OperatingHours != null)
        {
            // Hours are configured but the day resolves to nothing — closed.
            return false;
        }

        var overlapping = dayBookings
            .Where(b => MatchesPitch(b, venue, pitch))
            .Where(b => Overlaps(b.StartTime, b.Duration, start, end))
            .ToList();
        var overlappingPerms = dayPermanents
            .Where(p => MatchesPitch(p, venue, pitch))
            .Where(p => Overlaps(p.StartTime, p.Duration, start, end))
            .ToList();

        var isSubdividable = pitch.ParentSize != null && (pitch.SubSizes?.Count ?? 0) > 0;
        if (!isSubdividable)
            return overlapping.Count == 0 && overlappingPerms.Count == 0;

        var capacity = PitchSizes.CapacityOf(pitch);
        var usedUnits = overlapping.Sum(b => PitchSizes.WeightOf(b.PitchSize ?? pitch.ParentSize))
                      + overlappingPerms.Sum(p => PitchSizes.WeightOf(p.PitchSize ?? pitch.ParentSize));
        var smallestWeight = PitchSizes.OfferedSizes(pitch).Min(PitchSizes.WeightOf);
        return usedUnits + smallestWeight <= capacity;
    }

    private static bool Overlaps(string? existingStart, int existingDuration, TimeSpan start, TimeSpan end)
    {
        if (string.IsNullOrEmpty(existingStart) || !TimeSpan.TryParse(existingStart, out var existing))
            return false;
        var existingEnd = existing + TimeSpan.FromMinutes(existingDuration);
        return start < existingEnd && end > existing;
    }
}
