using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// What cancelling a booking does to the money already paid on it, per the venue's rule:
/// cancelled at least <see cref="Venue.FreeCancelHours"/> before the start → everything paid
/// is refunded; later than that → it is kept (the deposit covers the lost slot).
///
/// The person cancelling at the venue can override it either way ("refund all" / "keep");
/// a player cancelling in the app always gets the rule.
/// </summary>
public static class CancellationPolicy
{
    public const string Policy = "policy";
    public const string All = "all";
    public const string None = "none";

    public static bool IsValidChoice(string? choice) => choice is null or Policy or All or None;

    /// <summary>True when, at <paramref name="nowUtc"/>, the booking is still inside its free-cancellation period.</summary>
    public static bool IsFreeToCancel(Booking booking, Venue venue, DateTime nowUtc)
    {
        var start = PaymentDeadline.SlotStartUtc(booking.Date, booking.StartTime);
        // No start time to measure from: treat as late. Keeping money is the reversible side —
        // the owner can still refund by hand; a refund recorded in error is harder to explain.
        if (start is null) return false;
        return start.Value - nowUtc >= TimeSpan.FromHours(Math.Max(0, venue.FreeCancelHours));
    }

    /// <summary>How much to refund for a cancellation, given the choice made.</summary>
    public static double RefundFor(Booking booking, Venue venue, string? choice, DateTime nowUtc) =>
        booking.AmountPaid <= PaymentLedger.Epsilon ? 0
        : (choice ?? Policy) switch
        {
            All => booking.AmountPaid,
            None => 0,
            _ => IsFreeToCancel(booking, venue, nowUtc) ? booking.AmountPaid : 0,
        };
}
