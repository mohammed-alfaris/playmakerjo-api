namespace SportsVenueApi.Constants;

public static class PlatformConstants
{
    public const double SystemFeePercentage = 5.0;  // 5% platform fee

    /// <summary>
    /// "Today" in Jordan local time (UTC+3, no DST). Use this instead of
    /// <c>DateTime.UtcNow.Date</c> whenever you are comparing a user-submitted
    /// calendar date against the server's notion of "now" — the server runs in
    /// UTC, but bookings and operating hours are reasoned about in Jordan
    /// local time. Without this helper, evenings in Jordan (after 21:00 local)
    /// see "today" rejected as "in the past" because UTC has already moved on.
    /// </summary>
    public static DateTime JordanToday() => DateTime.UtcNow.AddHours(JordanUtcOffsetHours).Date;

    /// <summary>
    /// Jordan abolished daylight saving in 2022 and sits permanently at UTC+3, so a single
    /// constant is correct rather than a lazy approximation. Extracted so the one place
    /// that converts a stored slot ("2026-08-04" + "18:00", both Jordan-local) into a real
    /// UTC instant cannot drift from <see cref="JordanToday"/>.
    /// </summary>
    public const int JordanUtcOffsetHours = 3;
}
