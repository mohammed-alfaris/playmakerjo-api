using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;

namespace SportsVenueApi.Constants;

/// <summary>
/// Capacity-unit math for subdividable pitches.
///
/// One unit = one "quarter" of an 11-aside field. Weights:
///   11-aside = 4, 8/7-aside = 2, 6/5-aside = 1.
///
/// Split rules (what sub-sizes an owner may offer under each parent):
///   11 with half=8 → may offer {8, 6}
///   11 with half=7 → may offer {7, 5}
///   8              → may offer {6}
///   7              → may offer {5}
///   6, 5           → leaves, no sub-sizes
/// </summary>
public static class PitchSizes
{
    public static readonly string[] All = ["5", "6", "7", "8", "11"];

    public static readonly Dictionary<string, int> UnitWeight = new()
    {
        { "11", 4 },
        { "8",  2 },
        { "7",  2 },
        { "6",  1 },
        { "5",  1 },
    };

    /// <summary>Returns the allowed sub-size(s) for the given parent + half-size (when applicable).</summary>
    public static HashSet<string> AllowedSubSizes(string parent, string? half = null)
    {
        return parent switch
        {
            "11" => half switch
            {
                "8" => ["8", "6"],
                "7" => ["7", "5"],
                _    => ["8", "7", "6", "5"], // unresolved half → accept any, validated elsewhere
            },
            "8" => ["6"],
            "7" => ["5"],
            _   => [],
        };
    }

    /// <summary>Weight of a booking of the given size (1 if unknown).</summary>
    public static int WeightOf(string? size) =>
        size != null && UnitWeight.TryGetValue(size, out var w) ? w : 1;

    /// <summary>Total capacity units the venue's physical field has.</summary>
    public static int CapacityOf(Venue v) =>
        v.ParentSize != null && UnitWeight.TryGetValue(v.ParentSize, out var w) ? w : 1;

    /// <summary>Capacity units for a single pitch (4 for 11-aside, 1 for non-subdividable).</summary>
    public static int CapacityOf(PitchDto p) =>
        p.ParentSize != null && UnitWeight.TryGetValue(p.ParentSize, out var w) ? w : 1;

    /// <summary>
    /// Sport-aware capacity. For football, prefers the per-sport parentSize
    /// (sports_config["football"].parentSize) over the venue-level legacy field.
    /// Falls back to 1 for non-subdividable sports / legacy venues.
    /// </summary>
    public static int CapacityOfForSport(Venue v, string? sport)
    {
        if (sport == null || !string.Equals(sport, "football", StringComparison.OrdinalIgnoreCase))
            return CapacityOf(v);
        var cfg = v.SportsConfig.GetValueOrDefault("football");
        var parent = cfg?.ParentSize ?? v.ParentSize;
        return parent != null && UnitWeight.TryGetValue(parent, out var w) ? w : 1;
    }

    /// <summary>All sizes bookable at this venue (parent + sub-sizes).</summary>
    public static List<string> OfferedSizes(Venue v)
    {
        if (v.ParentSize == null) return []; // legacy — caller falls back to single-size logic
        var set = new HashSet<string> { v.ParentSize };
        foreach (var s in v.SubSizes) set.Add(s);
        return [.. set];
    }

    /// <summary>All sizes bookable on a single pitch (parent + sub-sizes, or empty if non-subdividable).</summary>
    public static List<string> OfferedSizes(PitchDto p)
    {
        if (p.ParentSize == null) return [];
        var set = new HashSet<string> { p.ParentSize };
        foreach (var s in p.SubSizes) set.Add(s);
        return [.. set];
    }

    /// <summary>
    /// Sport-aware variant: subdivision is a football-only concept, and per-sport
    /// config (sports_config["football"]) takes precedence over the venue-level
    /// legacy fields. Returns an empty list when the sport doesn't subdivide or
    /// the venue hasn't opted in.
    /// </summary>
    public static List<string> OfferedSizesForSport(Venue v, string? sport)
    {
        if (sport == null || !string.Equals(sport, "football", StringComparison.OrdinalIgnoreCase))
            return [];

        var cfg = v.SportsConfig.GetValueOrDefault("football");
        var parent = cfg?.ParentSize ?? v.ParentSize;
        if (parent == null) return [];

        var subs = cfg?.SubSizes ?? v.SubSizes;
        var set = new HashSet<string> { parent };
        if (subs != null)
        {
            foreach (var s in subs) set.Add(s);
        }
        return [.. set];
    }

    /// <summary>Validate subSizes for a pitch (football subdivision rules).</summary>
    public static string? ValidateSubSizes(PitchDto p) =>
        p.ParentSize == null
            ? null
            : ValidateSubSizes(p.ParentSize, p.SubSizes);

    /// <summary>
    /// Resolve a venue into its list of pitches. When <c>pitches</c> is populated,
    /// return those as-is. Otherwise synthesise one implicit pitch per sport from
    /// the legacy venue-level fields (sportsConfig + parentSize/subSizes/sizePrices),
    /// so every caller sees a uniform pitch list regardless of legacy vs. new data.
    /// </summary>
    public static List<PitchDto> ResolvedPitches(Venue v)
    {
        if (v.Pitches.Count > 0)
            return v.Pitches;

        var cfgMap = v.SportsConfig;
        return v.Sports.Select(sp =>
        {
            var cfg = cfgMap.GetValueOrDefault(sp);
            var isFootball = string.Equals(sp, "football", StringComparison.OrdinalIgnoreCase);
            return new PitchDto
            {
                Id = $"legacy-{v.Id}-{sp}",
                Name = sp,
                Sport = sp,
                ParentSize = cfg?.ParentSize ?? (isFootball ? v.ParentSize : null),
                SubSizes = cfg?.SubSizes ?? (isFootball ? v.SubSizes : []),
                SizePrices = cfg?.SizePrices ?? (isFootball ? v.SizePrices : []),
                PricePerHour = cfg?.PricePerHour ?? v.PricePerHour,
                OperatingHours = cfg?.OperatingHours,
            };
        }).ToList();
    }

    /// <summary>
    /// Pick the default pitch id for a sport when the caller omitted it. Returns the
    /// single matching pitch's id when exactly one pitch offers that sport, otherwise
    /// null (the caller should then return PITCH_REQUIRED to force the client to pick).
    /// </summary>
    public static string? PickDefaultPitchId(Venue v, string? sport)
    {
        var pitches = ResolvedPitches(v);
        var matches = pitches.Where(p =>
            sport == null || string.Equals(p.Sport, sport, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0].Id : null;
    }

    /// <summary>
    /// Validate a venue's subSizes against its parent (and optional half-size hint
    /// — the first entry in subSizes is treated as the half choice).
    /// Returns an error string or null if valid.
    /// </summary>
    public static string? ValidateSubSizes(string parent, List<string> subSizes)
    {
        if (!UnitWeight.ContainsKey(parent))
            return $"Invalid parentSize: '{parent}'. Must be one of 5, 6, 7, 8, 11.";

        if (parent is "5" or "6")
        {
            if (subSizes.Count > 0)
                return $"{parent}-aside cannot be subdivided.";
            return null;
        }

        foreach (var s in subSizes)
        {
            if (!UnitWeight.ContainsKey(s))
                return $"Invalid subSize: '{s}'.";
        }

        if (parent == "11")
        {
            // Owner must pick one half-size (8 or 7) to enable splits. The quarter
            // sub-size is determined by the half-size: 8→6, 7→5.
            var half = subSizes.FirstOrDefault(s => s is "8" or "7");
            var quarter = subSizes.FirstOrDefault(s => s is "6" or "5");

            if (subSizes.Count(s => s is "8" or "7") > 1)
                return "Pick only one half-size: either 8-aside or 7-aside.";

            if (half == "8" && quarter == "5")
                return "When half-size is 8-aside, the quarter option must be 6-aside (not 5).";
            if (half == "7" && quarter == "6")
                return "When half-size is 7-aside, the quarter option must be 5-aside (not 6).";
            if (half == null && quarter != null)
                return "Pick a half-size (8-aside or 7-aside) before offering the quarter size.";
        }
        else if (parent == "8")
        {
            foreach (var s in subSizes)
                if (s != "6") return "8-aside can only be subdivided into 6-aside.";
        }
        else if (parent == "7")
        {
            foreach (var s in subSizes)
                if (s != "5") return "7-aside can only be subdivided into 5-aside.";
        }

        return null;
    }
}
