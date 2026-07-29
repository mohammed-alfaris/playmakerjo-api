using System.Text.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>Why a day has no bookable window — or that it has one.</summary>
public enum DayHoursStatus
{
    /// <summary>No operating-hours map at all. Nothing to enforce.</summary>
    NotConfigured,
    /// <summary>
    /// A map exists but carries no entry for this weekday. NOT the same as closed:
    /// the dashboard editor always writes all seven days, so a gap means the row
    /// predates the editor or was created through the API — not that the owner
    /// shut the day. Treating it as closed takes legacy venues offline entirely.
    /// </summary>
    DayNotListed,
    /// <summary>The day carries an explicit <c>closed: true</c>. The owner meant it.</summary>
    Closed,
    /// <summary>A usable open/close window.</summary>
    Open
}

/// <param name="Status">Which of the four cases this day falls into.</param>
/// <param name="Window">Set only when <paramref name="Status"/> is <see cref="DayHoursStatus.Open"/>.</param>
public readonly record struct DayHoursResult(DayHoursStatus Status, OperatingHoursInfo? Window);

/// <summary>The outcome of testing one slot against a day's operating hours.</summary>
public enum SlotHoursVerdict
{
    Allowed,
    /// <summary>open/close present but unparseable — the venue's data is broken.</summary>
    Misconfigured,
    OutsideHours,
    Closed
}

/// <param name="Open">Window start, for the caller's error message. Empty unless <paramref name="Verdict"/> is OutsideHours.</param>
/// <param name="Close">Window end, likewise.</param>
public readonly record struct SlotHoursCheck(SlotHoursVerdict Verdict, string Open, string Close);

/// <summary>
/// Shared availability primitives: operating-hours resolution, pitch matching
/// for bookings/permanents, and the slot-capacity check used by the public
/// availability search.
/// </summary>
public static class AvailabilityHelper
{
    /// <summary>
    /// Resolves one weekday out of an operating-hours map, keeping the three
    /// distinct reasons a day can yield no window apart. <see cref="ResolveHoursForDay"/>
    /// collapses them all to null, which is what let a venue whose map simply
    /// omitted a weekday be reported as closed on that day forever.
    /// </summary>
    public static DayHoursResult ResolveDayHours(Dictionary<string, object>? hoursMap, string dayName)
    {
        if (hoursMap == null) return new(DayHoursStatus.NotConfigured, null);

        // The dashboard writes keys as full day names ("monday", "tuesday", ...)
        // while older seed data used 3-letter abbreviations ("mon", "tue", ...).
        // Accept both so legacy venues and newly-edited ones keep working.
        var dayFull = dayName.ToLower();
        var dayShort = dayFull.Length >= 3 ? dayFull[..3] : dayFull;
        if (!hoursMap.TryGetValue(dayFull, out var dayHoursObj)
            && !hoursMap.TryGetValue(dayShort, out dayHoursObj))
            return new(DayHoursStatus.DayNotListed, null);

        var dayHoursJson = JsonSerializer.Serialize(dayHoursObj);
        var dayHours = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dayHoursJson);
        if (dayHours == null) return new(DayHoursStatus.DayNotListed, null);

        // Honour the "closed: true" flag written by the dashboard editor.
        if (dayHours.TryGetValue("closed", out var closedEl)
            && closedEl.ValueKind == JsonValueKind.True)
            return new(DayHoursStatus.Closed, null);

        string GetStr(string k, string fallback)
            => dayHours.TryGetValue(k, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;

        return new(DayHoursStatus.Open, new OperatingHoursInfo
        {
            Open = GetStr("open", "08:00"),
            Close = GetStr("close", "22:00")
        });
    }

    /// <summary>
    /// Pitch hours override the venue's for a day when the pitch actually offers a
    /// window for it. An explicit <c>closed</c> on the pitch stands rather than
    /// falling through to the venue — otherwise a pitch could never be shut on a
    /// day the venue is open.
    /// </summary>
    public static DayHoursResult ResolveEffectiveDayHours(Venue venue, PitchDto pitch, string dayName)
    {
        var venueResult = ResolveDayHours(venue.OperatingHours, dayName);
        if (pitch.OperatingHours == null) return venueResult;

        DayHoursResult pitchResult;
        try
        {
            var json = JsonSerializer.Serialize(pitch.OperatingHours);
            var map = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (map == null) return venueResult;
            pitchResult = ResolveDayHours(map, dayName);
        }
        catch
        {
            return venueResult;
        }

        return pitchResult.Status switch
        {
            DayHoursStatus.Open => pitchResult,
            DayHoursStatus.Closed => pitchResult,
            _ => venueResult
        };
    }

    /// <summary>
    /// Does [start, start+duration) sit inside the pitch's window for this day?
    ///
    /// A close at or before the open means the venue shuts the FOLLOWING day —
    /// 18:00–02:00 is a normal evening pitch schedule here, not an empty window.
    /// Extending the window by 24h is the whole reason this lives in one place:
    /// the check used to be written out at four call sites and only one of them
    /// wrapped, so overnight venues were bookable through the permanent-booking
    /// screen and rejected everywhere else.
    /// </summary>
    public static SlotHoursCheck CheckSlotAgainstHours(
        Venue venue, PitchDto pitch, string dayName, TimeSpan start, int durationMinutes)
    {
        var resolved = ResolveEffectiveDayHours(venue, pitch, dayName);

        switch (resolved.Status)
        {
            case DayHoursStatus.Closed:
                return new(SlotHoursVerdict.Closed, "", "");
            case DayHoursStatus.NotConfigured:
            case DayHoursStatus.DayNotListed:
                return new(SlotHoursVerdict.Allowed, "", "");
        }

        var window = resolved.Window!;
        if (!TimeSpan.TryParse(window.Open, out var open) || !TimeSpan.TryParse(window.Close, out var close))
            return new(SlotHoursVerdict.Misconfigured, window.Open, window.Close);

        if (close <= open) close += TimeSpan.FromHours(24);

        var end = start + TimeSpan.FromMinutes(durationMinutes);
        if (start < open || end > close)
            return new(SlotHoursVerdict.OutsideHours, window.Open, window.Close);

        return new(SlotHoursVerdict.Allowed, window.Open, window.Close);
    }

    /// <summary>
    /// Legacy shape kept for the read-only display paths in VenuesController, which
    /// only ever echo a window into a response and do not care why one is absent.
    /// New enforcement code should use <see cref="ResolveDayHours"/>.
    /// </summary>
    public static OperatingHoursInfo? ResolveHoursForDay(Dictionary<string, object>? hoursMap, string dayName)
        => ResolveDayHours(hoursMap, dayName).Window;

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
        // Same verdict the booking endpoints use, so the availability grid and the
        // create call can no longer disagree about whether a slot is offerable.
        if (CheckSlotAgainstHours(venue, pitch, dayName, start, duration).Verdict != SlotHoursVerdict.Allowed)
            return false;

        var end = start + TimeSpan.FromMinutes(duration);

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
