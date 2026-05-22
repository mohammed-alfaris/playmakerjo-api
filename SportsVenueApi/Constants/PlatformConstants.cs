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
    public static DateTime JordanToday() => DateTime.UtcNow.AddHours(3).Date;
}
