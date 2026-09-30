using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// The only sanctioned way to record that money has arrived.
///
/// Every write path that touches <c>booking.AmountPaid</c> goes through <see cref="Settle"/>,
/// which moves the field AND produces the matching ledger row in one step. That coupling is
/// deliberate: it makes the ledger invariant true by construction rather than by everyone
/// remembering. The alternative — each controller setting the field and then, separately,
/// adding a Payment — is exactly how audit trails end up with holes in them, because the
/// second half is easy to forget on the fifth code path added a year later.
///
/// Rows are deltas. Settling a booking that already has a 10 JOD deposit against a 50 JOD
/// total writes 40, not 50, so the rows sum to the field rather than to double it.
/// </summary>
public static class PaymentLedger
{
    /// <summary>
    /// JOD is quoted to three decimals (1 dinar = 1000 fils), so anything below half a fil
    /// is floating-point noise from the percentage arithmetic in deposit calculation, not a
    /// real balance. Without this a "fully paid" booking can be left owing 3e-9 JOD forever
    /// and keep offering the owner a Settle button that does nothing.
    /// </summary>
    public const double Epsilon = 0.0005;

    /// <summary>
    /// Raise the booking's paid amount to <paramref name="target"/> and return the row that
    /// records the difference. The caller adds the returned row to the context and saves it
    /// in the SAME SaveChanges as the booking — the field and its evidence must commit
    /// together or not at all.
    ///
    /// Returns null when nothing moved, which is the normal outcome of a duplicate submit or
    /// a second approval attempt. Recording a zero would be worse than recording nothing:
    /// it puts a payment event in the owner's history that never happened.
    /// </summary>
    /// <param name="target">The new total paid for this booking, not the increment.</param>
    /// <param name="kind">"deposit" | "balance" | "full" — what this delta settled.</param>
    /// <param name="actorUserId">Who performed the act. Never the customer: they cannot record their own payment.</param>
    public static Payment? Settle(
        Booking booking,
        double target,
        string kind,
        string? actorUserId,
        string? note = null,
        string? method = null)
    {
        var delta = Math.Round(target - booking.AmountPaid, 3);
        if (delta <= Epsilon) return null;

        booking.AmountPaid = Math.Round(target, 3);
        booking.DepositPaid = true;

        return new Payment
        {
            BookingId = booking.Id,
            PlayerId = booking.PlayerId,
            CustomerId = booking.CustomerId,
            VenueId = booking.VenueId,
            RecordedByUserId = actorUserId,
            Amount = delta,
            // A counter booking with no method recorded is cash by definition — the money
            // was handed over in the room. An app booking always carries its method.
            Method = method ?? booking.PaymentMethod ?? (booking.IsManual ? "cash" : null),
            Kind = kind,
            // Every row this helper writes describes money already received. There is no
            // pending state here: a payment we are merely expecting is the unpaid remainder
            // of the booking, and it is represented by the absence of a row.
            Status = "paid",
            Note = note,
            Date = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Record money going back to the customer, or a correction of an amount recorded wrongly.
    ///
    /// The ledger is append-only, so a refund is a new row with a NEGATIVE amount rather than
    /// an edit of the row it undoes: the history keeps both what was taken and what was given
    /// back, and <c>SUM(payments.amount) == booking.AmountPaid</c> still holds. Every total in
    /// the system sums the rows, so "collected" becomes net of refunds without anyone having
    /// to remember to subtract them.
    ///
    /// Returns an error message instead of a row when the amount is not refundable: nothing,
    /// or more than has been paid on this booking.
    /// </summary>
    /// <param name="kind">"refund" (money returned) or "correction" (an amount recorded by mistake).</param>
    /// <param name="actorUserId">Who decided it. Null when a policy did — e.g. a player's cancellation.</param>
    public static (Payment? Row, string? Error) Refund(
        Booking booking,
        double amount,
        string kind,
        string? actorUserId,
        string? note = null,
        string? method = null)
    {
        amount = Math.Round(amount, 3);
        if (amount <= Epsilon)
            return (null, "Enter an amount to refund.");
        if (amount > booking.AmountPaid + Epsilon)
            return (null, $"Only {booking.AmountPaid:0.###} JOD has been paid on this booking.");

        booking.AmountPaid = Math.Round(booking.AmountPaid - amount, 3);
        if (booking.AmountPaid <= Epsilon)
        {
            booking.AmountPaid = 0;
            booking.DepositPaid = false;
        }

        return (new Payment
        {
            BookingId = booking.Id,
            PlayerId = booking.PlayerId,
            CustomerId = booking.CustomerId,
            VenueId = booking.VenueId,
            RecordedByUserId = actorUserId,
            Amount = -amount,
            Method = method ?? booking.PaymentMethod ?? (booking.IsManual ? "cash" : null),
            Kind = kind,
            // "paid" as for every row: it records a completed movement of money. The sign and
            // the kind say which way it went; a separate status would hide refunds from the
            // Payments page's status filter and break its totals.
            Status = "paid",
            Note = note,
            Date = DateTime.UtcNow,
        }, null);
    }
}
