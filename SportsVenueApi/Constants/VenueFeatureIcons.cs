namespace SportsVenueApi.Constants;

/// <summary>
/// The icons a catalog feature may carry.
///
/// A fixed set rather than uploaded images, because both clients render these natively —
/// the dashboard maps each key to a lucide icon, the mobile app to a Material Symbol — and a
/// native icon is crisp in every size and theme where an uploaded PNG is not. The cost is that
/// a brand-new icon needs a client release, so both clients fall back to a generic icon for a
/// key they do not know: an older app build keeps rendering after a key is added here.
///
/// Keep in step with playmakerjo-dashboard src/lib/featureIcons.tsx and
/// playmakerjo-app lib/core/theme/feature_icons.dart.
/// </summary>
public static class VenueFeatureIcons
{
    public static readonly IReadOnlyList<string> All =
    [
        "parking", "shower", "changing_room", "locker", "wifi", "floodlights",
        "cafe", "water", "seating", "prayer_room", "first_aid", "accessible",
        "air_conditioning", "restroom", "equipment", "cctv", "kids_area", "indoor",
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsValid(string? key) => key != null && Known.Contains(key);
}
