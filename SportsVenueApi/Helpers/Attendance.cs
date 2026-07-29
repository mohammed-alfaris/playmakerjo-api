using System.Linq.Expressions;
using SportsVenueApi.Constants;
using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// "Did this person actually turn up?" — in one place.
///
/// Nothing in the system can observe attendance: there is no gate, no check-in, no sensor.
/// The only source of truth is a human tapping something, and an owner has no reason to tap
/// "completed" when the evening went fine. So attendance is DERIVED:
///
///     completed  OR  (confirmed AND the slot is in the past)
///
/// Presence assumed, absence declared. Counting only "completed" would show near-zero
/// attendance forever and quietly break every number built on it.
///
/// This rule was written out by hand in three places and had already drifted once — the
/// "stopped coming" segment and the win-back report disagreed about who had gone quiet
/// (GAP-16). Reviews then used a fourth, stricter rule of its own (GAP-11), which locked
/// most genuine attendees out of reviewing because their booking correctly stays
/// "confirmed". One definition, three callers.
/// </summary>
public static class Attendance
{
    /// <summary>In-memory form, for rows already loaded.</summary>
    public static bool Attended(DateTime bookingDate, string status, DateTime today) =>
        status == "completed" || (status == "confirmed" && bookingDate.Date < today);

    /// <inheritdoc cref="Attended(DateTime, string, DateTime)"/>
    public static bool Attended(DateTime bookingDate, string status) =>
        Attended(bookingDate, status, PlatformConstants.JordanToday());

    /// <summary>
    /// Query form, for filtering in SQL. EF cannot translate a method call, so the same rule
    /// has to be expressible as an expression tree — keep the two bodies identical.
    ///
    /// Note the date comparison is <c>b.Date &lt; today</c> rather than <c>b.Date.Date</c>:
    /// EF emits DATE(date) for the latter, which cannot use an index.
    /// </summary>
    public static Expression<Func<Booking, bool>> AttendedExpr(DateTime today) =>
        b => b.Status == "completed" || (b.Status == "confirmed" && b.Date < today);
}
