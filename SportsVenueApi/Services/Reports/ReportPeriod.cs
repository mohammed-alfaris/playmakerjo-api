using System.Globalization;
using SportsVenueApi.Constants;

namespace SportsVenueApi.Services.Reports;

/// <summary>
/// A reporting period: two Amman calendar dates, both inclusive.
///
/// Every report is keyed on Amman days because that is the day the owner lives in — a
/// payment taken at 01:30 on Friday night belongs to Friday's takings in their head, and
/// to Thursday in UTC. Bookings already store the Amman date (<c>Booking.Date</c>), so they
/// compare directly; UTC instants (payments, created_at) go through <see cref="FromUtc"/>
/// and <see cref="ToUtcExclusive"/>.
/// </summary>
public sealed record ReportPeriod(DateTime From, DateTime To)
{
    /// <summary>Long enough for a year-on-year view, short enough to stay cheap.</summary>
    public const int MaxDays = 366;

    public int Days => (int)(To - From).TotalDays + 1;

    public DateTime FromUtc => From.AddHours(-PlatformConstants.JordanUtcOffsetHours);
    public DateTime ToUtcExclusive => To.AddDays(1).AddHours(-PlatformConstants.JordanUtcOffsetHours);

    /// <summary>The same number of days immediately before this one — what "compare" compares with.</summary>
    public ReportPeriod Previous() => new(From.AddDays(-Days), From.AddDays(-1));

    public bool Contains(DateTime ammanDate) => ammanDate.Date >= From && ammanDate.Date <= To;

    /// <summary>The Amman calendar date of a UTC instant.</summary>
    public static DateTime AmmanDateOf(DateTime utc) => utc.AddHours(PlatformConstants.JordanUtcOffsetHours).Date;

    public IEnumerable<DateTime> Dates()
    {
        for (var d = From; d <= To; d = d.AddDays(1)) yield return d;
    }

    /// <summary>
    /// Parse "yyyy-MM-dd" bounds. Missing bounds default to this month so far — the view an
    /// owner opens the page wanting. Returns an error message instead of throwing.
    /// </summary>
    public static (ReportPeriod? Period, string? Error) Parse(string? from, string? to)
    {
        var today = PlatformConstants.JordanToday();
        var start = new DateTime(today.Year, today.Month, 1);
        var end = today;

        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!DateTime.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
                return (null, "from must be a date like 2026-09-01.");
        }
        if (!string.IsNullOrWhiteSpace(to))
        {
            if (!DateTime.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out end))
                return (null, "to must be a date like 2026-09-30.");
        }
        else if (!string.IsNullOrWhiteSpace(from) && start > end)
        {
            end = start;
        }

        if (end < start) return (null, "The period ends before it starts.");
        var period = new ReportPeriod(start.Date, end.Date);
        if (period.Days > MaxDays) return (null, $"A report can cover at most {MaxDays} days.");
        return (period, null);
    }
}
