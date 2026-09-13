using System.Text;
using System.Text.RegularExpressions;
using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// Turns what an owner submitted as their venue's features into what gets stored.
///
/// Two lists come in: ids picked from the catalog, and labels the owner typed. The typed labels
/// are the untrusted part — free text from a form — so they are trimmed, whitespace-collapsed,
/// de-duplicated and capped here. A typed label that exactly matches a catalog feature's English
/// or Arabic name becomes that catalog feature instead of a duplicate custom label: an owner who
/// types "Parking" by hand gets the real feature, with its icon, and players filtering by
/// parking find the venue. Left as free text it would be invisible to the filter.
///
/// Mirrored by playmakerjo-dashboard src/lib/venueFeatureRules.ts so the form rejects the same
/// input before it is sent. The server stays the authority.
/// </summary>
public static partial class VenueFeatureRules
{
    public const int MaxCustomLabelLength = 40;
    public const int MaxCustomPerVenue = 15;
    public const int MaxFeatureIdsPerVenue = 50;

    /// <summary>
    /// Catalog ids are lowercase slugs. Enforcing the shape before an id reaches a query matters:
    /// the venue filter matches ids inside a JSON text column with a substring search, and a
    /// value carrying '%' or '_' or a quote could otherwise widen that match.
    /// </summary>
    [GeneratedRegex("^[a-z0-9-]{1,40}$")]
    private static partial Regex IdShape();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static bool IsWellFormedId(string? id) => id != null && IdShape().IsMatch(id);

    /// <summary>Trim and collapse internal runs of whitespace to one space.</summary>
    public static string CleanLabel(string? raw) =>
        raw == null ? "" : Whitespace().Replace(raw.Trim(), " ");

    /// <summary>"Changing rooms" → "vf-changing-rooms". Empty when nothing usable remains.</summary>
    public static string Slugify(string name)
    {
        var sb = new StringBuilder("vf-");
        var lastDash = true;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var slug = sb.ToString().TrimEnd('-');
        if (slug.Length > 36) slug = slug[..36].TrimEnd('-');
        return slug == "vf" ? "" : slug;
    }

    /// <summary>
    /// Parse a "vf-a,vf-b" filter. Returns null when every id is well-formed (the list may be
    /// empty), otherwise the message to send back.
    /// </summary>
    public static string? ParseFilter(string? csv, out List<string> ids)
    {
        ids = (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var bad = ids.FirstOrDefault(id => !IsWellFormedId(id));
        return bad == null ? null : $"Invalid feature id '{bad}'.";
    }

    public sealed record Normalized(List<string> FeatureIds, List<string> CustomFeatures, string? Error)
    {
        public static Normalized Fail(string message) => new([], [], message);
    }

    /// <param name="requestedIds">Catalog ids the owner ticked.</param>
    /// <param name="requestedCustom">Labels the owner typed.</param>
    /// <param name="catalog">Every catalog feature, active or not, keyed by id.</param>
    /// <param name="alreadyAttached">
    /// Ids the venue already has. A feature an admin has since retired may be KEPT on a venue that
    /// chose it — editing an old venue must not start failing because of an unrelated catalog
    /// change — but it may not be newly added.
    /// </param>
    public static Normalized Normalize(
        IEnumerable<string>? requestedIds,
        IEnumerable<string>? requestedCustom,
        IReadOnlyDictionary<string, VenueFeature> catalog,
        IReadOnlyCollection<string> alreadyAttached)
    {
        var ids = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        bool Selectable(VenueFeature f) => f.IsActive || alreadyAttached.Contains(f.Id);

        foreach (var id in requestedIds ?? [])
        {
            if (!catalog.TryGetValue(id, out var feature))
                return Normalized.Fail($"Unknown feature '{id}'.");
            if (!Selectable(feature))
                return Normalized.Fail($"'{feature.NameEn}' is no longer offered and cannot be added.");
            if (seenIds.Add(id)) ids.Add(id);
        }

        // A typed label equal to a catalog name — in either language, ignoring case — is that
        // feature. Only features the owner could have ticked are matched; a retired one stays
        // a plain label rather than sneaking back in.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in catalog.Values.Where(Selectable))
        {
            byName.TryAdd(f.NameEn, f.Id);
            byName.TryAdd(f.NameAr, f.Id);
        }

        var custom = new List<string>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in requestedCustom ?? [])
        {
            var label = CleanLabel(raw);
            if (label.Length == 0) continue;
            if (label.Length > MaxCustomLabelLength)
                return Normalized.Fail($"Custom features must be at most {MaxCustomLabelLength} characters.");

            if (byName.TryGetValue(label, out var catalogId))
            {
                if (seenIds.Add(catalogId)) ids.Add(catalogId);
                continue;
            }

            if (seenLabels.Add(label)) custom.Add(label);
        }

        if (custom.Count > MaxCustomPerVenue)
            return Normalized.Fail($"A venue can list at most {MaxCustomPerVenue} custom features.");
        if (ids.Count > MaxFeatureIdsPerVenue)
            return Normalized.Fail($"A venue can list at most {MaxFeatureIdsPerVenue} features.");

        return new Normalized(ids, custom, null);
    }
}
