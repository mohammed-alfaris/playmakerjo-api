using SportsVenueApi.Constants;

namespace SportsVenueApi.Helpers;

/// <summary>How long an unpaid app booking may hold a slot. All values in minutes.</summary>
/// <param name="WindowMinutes">Normal grace to complete a CliQ transfer and upload the screenshot.</param>
/// <param name="SlotBufferMinutes">How far ahead of kick-off the slot must be released so somebody else can still take it.</param>
/// <param name="MinimumMinutes">Floor nobody drops below, however close to kick-off they booked.</param>
public sealed record ExpiryPolicy(int WindowMinutes, int SlotBufferMinutes, int MinimumMinutes)
{
    /// <summary>
    /// Two hours to pay, released half an hour before kick-off, never less than ten minutes.
    ///
    /// Two hours because CliQ is a real bank transfer: the customer leaves the app, opens a
    /// banking app, transfers, screenshots and comes back. Thirty minutes would cancel
    /// people who are genuinely paying.
    /// </summary>
    public static readonly ExpiryPolicy Default = new(WindowMinutes: 120, SlotBufferMinutes: 30, MinimumMinutes: 10);
}

/// <summary>
/// Works out the instant at which an unpaid booking stops holding its slot.
///
/// Pure and side-effect free so the interesting cases can be tested without a database, a
/// clock seam or a background thread — there is no clock abstraction anywhere in this
/// codebase (49 direct <c>DateTime.UtcNow</c> call sites), and adding one just to test this
/// would be a far larger change than the feature.
/// </summary>
public static class PaymentDeadline
{
    /// <summary>
    /// Converts a stored slot into a real UTC instant.
    ///
    /// <paramref name="bookingDate"/> is a naive Jordan-local calendar date and
    /// <paramref name="startTime"/> a naive Jordan-local "HH:mm" — the schema has no
    /// combined start instant (Booking.cs:48-53). Returns null when the time is missing or
    /// unparseable, which both conflict scans already tolerate rather than throw on.
    /// </summary>
    public static DateTime? SlotStartUtc(DateTime bookingDate, string? startTime)
    {
        if (string.IsNullOrWhiteSpace(startTime)) return null;
        if (!TimeSpan.TryParse(startTime, out var tod)) return null;
        // Guard against "25:00" and negative spans parsing successfully into nonsense.
        if (tod < TimeSpan.Zero || tod >= TimeSpan.FromHours(24)) return null;

        return bookingDate.Date.Add(tod).AddHours(-PlatformConstants.JordanUtcOffsetHours);
    }

    /// <summary>
    /// The deadline to stamp on a newly unpaid app booking, or null to leave it immune.
    ///
    /// Takes the EARLIER of "now + window" and "shortly before kick-off". A fixed window
    /// alone gets the perishable case exactly backwards: a slot booked at 09:00 for 18:00
    /// tonight would not become eligible until 11:00 with a 2h window — fine — but with the
    /// 12h window that first seems prudent it would not expire until 21:00, three hours
    /// after the game it was holding had already started. The slot term is what makes the
    /// job actually useful on the day that matters.
    ///
    /// The floor is what stops the opposite failure: without it, a booking made ten minutes
    /// before kick-off would be stamped with a deadline in the past and cancelled by the
    /// next sweep, seconds after the customer made it.
    /// </summary>
    public static DateTime Compute(DateTime nowUtc, DateTime bookingDate, string? startTime, ExpiryPolicy policy)
    {
        var deadline = nowUtc.AddMinutes(policy.WindowMinutes);

        var slotStart = SlotStartUtc(bookingDate, startTime);
        if (slotStart is { } start)
        {
            var releaseBy = start.AddMinutes(-policy.SlotBufferMinutes);
            if (releaseBy < deadline) deadline = releaseBy;
        }

        var floor = nowUtc.AddMinutes(policy.MinimumMinutes);
        return deadline < floor ? floor : deadline;
    }
}
