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
}
