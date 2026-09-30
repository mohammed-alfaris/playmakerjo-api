using System.Text;
using System.Text.RegularExpressions;

namespace SportsVenueApi.Helpers;

/// <summary>
/// Normalises Jordanian phone numbers to a single canonical form, <c>+9627XXXXXXXX</c>.
///
/// The phone is the natural key for a customer — <c>(owner_id, phone)</c> is unique — so
/// every accepted spelling of the same number MUST collapse to one string. Without this,
/// the same person typed as "0791234567", "+962 79 123 4567" and "٠٧٩١٢٣٤٥٦٧" becomes
/// three customers, each with a third of their history, and the whole feature is worthless.
///
/// Arabic-Indic digits are handled first and deliberately: most venue owners in Jordan type
/// on an Arabic keyboard, and nothing else in this codebase converts them.
/// </summary>
public static partial class PhoneNormalizer
{
    private const string CountryCode = "+962";

    /// <summary>Jordanian mobile prefixes: 77, 78, 79 (after the leading 7).</summary>
    [GeneratedRegex(@"^\+9627[789]\d{7}$")]
    private static partial Regex CanonicalMobile();

    /// <summary>
    /// Returns the canonical <c>+9627XXXXXXXX</c> form, or null when the input is not a
    /// recognisable Jordanian mobile number. Callers treat null as "do not create a
    /// customer" rather than as an error — a booking must never be blocked because someone
    /// gave a landline or a Syrian number.
    /// </summary>
    public static string? ToE164Jo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var digits = ToWesternDigits(raw);
        if (digits.Length == 0) return null;

        // Strip everything that is not a digit, keeping a leading + only to spot 00/+ forms.
        var sb = new StringBuilder(digits.Length);
        foreach (var ch in digits)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
        }
        var d = sb.ToString();
        if (d.Length == 0) return null;

        // 00962… → 962…
        if (d.StartsWith("00962", StringComparison.Ordinal)) d = d[2..];

        string national = d switch
        {
            // 962 7XXXXXXXX  (with or without a + that we already stripped)
            _ when d.StartsWith("9627", StringComparison.Ordinal) && d.Length == 12 => d[3..],
            // 07XXXXXXXX
            _ when d.StartsWith("07", StringComparison.Ordinal) && d.Length == 10 => d[1..],
            // 7XXXXXXXX
            _ when d.StartsWith("7", StringComparison.Ordinal) && d.Length == 9 => d,
            _ => "",
        };

        if (national.Length == 0) return null;

        var candidate = CountryCode + national;
        return CanonicalMobile().IsMatch(candidate) ? candidate : null;
    }

    /// <summary>True when the input normalises to a Jordanian mobile.</summary>
    public static bool IsJordanianMobile(string? raw) => ToE164Jo(raw) != null;

    /// <summary>
    /// Maps Arabic-Indic (٠-٩) and Eastern Arabic-Indic (۰-۹) digits to 0-9 and leaves
    /// everything else untouched. Runs before any parsing.
    /// </summary>
    private static string ToWesternDigits(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            sb.Append(ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')), // ٠..٩
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')), // ۰..۹
                _ => ch,
            });
        }
        return sb.ToString();
    }
}
