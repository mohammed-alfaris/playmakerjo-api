using System.Text.RegularExpressions;

namespace SportsVenueApi.Helpers;

/// <summary>
/// The venue's public link, playmakerjo.com/v/{slug}: lowercase Latin letters, digits and
/// single hyphens, 3–64 characters. Typed by an owner, so it is tidied rather than refused
/// where that is unambiguous (case, spaces).
/// </summary>
public static partial class VenueSlug
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex Shape();

    /// <summary>The tidied slug, or an error to show the owner.</summary>
    public static (string? Slug, string? Error) Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant().Replace(' ', '-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        if (s.Length < 3 || s.Length > 64)
            return (null, "The booking link must be 3 to 64 characters");
        if (!Shape().IsMatch(s))
            return (null, "The booking link may use only English letters, numbers and hyphens");
        return (s, null);
    }
}
