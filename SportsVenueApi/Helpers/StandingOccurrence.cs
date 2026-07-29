using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// A standing weekly reservation is a RULE, not a booking: "every Tuesday 20:00, Mohammed".
/// It blocks the slot every week and never becomes a row, so on its own there is nothing to
/// take money against, nothing to mark attended, and nothing that reaches the ledger.
///
/// "Recording this week" materialises one occurrence into a real booking. From that moment
/// the rule must stop blocking THAT DATE — the booking is doing the blocking now. Skip this
/// and the slot is consumed twice: invisible on a single-capacity pitch, but on a
/// subdividable one it eats two of four units and the owner loses a game he could have sold.
/// </summary>
public static class StandingOccurrence
{
    /// <summary>
    /// Drop the rules that already have a real booking on this date.
    ///
    /// Cancelled bookings do NOT count as materialised: if the owner cancels the recorded
    /// week, the standing rule has to resume blocking the slot, or a group that comes every
    /// Tuesday quietly loses its pitch to whoever books next.
    /// </summary>
    public static List<PermanentBooking> NotYetRecorded(
        IEnumerable<PermanentBooking> permanents,
        IEnumerable<Booking> bookingsOnThatDate)
    {
        var recorded = bookingsOnThatDate
            .Where(b => b.PermanentBookingId != null && b.Status != "cancelled")
            .Select(b => b.PermanentBookingId!)
            .ToHashSet(StringComparer.Ordinal);

        return permanents.Where(p => !recorded.Contains(p.Id)).ToList();
    }
}
