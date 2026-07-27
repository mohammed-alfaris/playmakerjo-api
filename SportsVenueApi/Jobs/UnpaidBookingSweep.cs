using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.Helpers;
using SportsVenueApi.Services;

namespace SportsVenueApi.Jobs;

/// <summary>What one pass did. Returned rather than logged-and-forgotten so tests can assert on it.</summary>
public sealed record SweepResult(int Considered, int Cancelled, int Notified)
{
    public static readonly SweepResult Nothing = new(0, 0, 0);
}

/// <summary>
/// Releases the slots held by app bookings whose payment deadline has passed.
///
/// All the logic lives here, in a plain Scoped service taking an explicit <c>nowUtc</c>,
/// while <see cref="BookingExpiryService"/> is a timer shell that owns no rules. That split
/// is what makes this testable: there is no clock abstraction anywhere in this codebase, so
/// a test calls SweepAsync directly with whatever instant it likes instead of waiting for a
/// background thread to tick.
///
/// SAFETY MODEL — read before changing the predicate.
///
/// The job cancels a row only if that row was explicitly ARMED with a
/// <c>payment_deadline_at</c>, and only the app-booking creation path arms anything. NULL
/// means immune. So the dangerous cases are not excluded by clauses that somebody has to
/// remember — they are unreachable:
///
///   • counter/phone bookings   — never armed, and they are MEANT to sit unpaid
///   • recurring series         — never armed; one abandoned checkout is 3 months of rows
///   • every pre-existing row   — column is NULL after the migration
///   • proof awaiting review    — disarmed on upload; the money may already have moved
///   • already-paid bookings    — disarmed on settle
///
/// The clauses below are defence in depth, not the safety argument.
/// </summary>
public class UnpaidBookingSweep
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<UnpaidBookingSweep> _logger;

    public UnpaidBookingSweep(
        AppDbContext db, NotificationService notifications, ILogger<UnpaidBookingSweep> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<SweepResult> SweepAsync(DateTime nowUtc, int batchSize, CancellationToken ct = default)
    {
        if (batchSize < 1) return SweepResult.Nothing;

        var candidates = await _db.Bookings
            .Where(b =>
                // The arming flag. Everything else is belt and braces.
                b.PaymentDeadlineAt != null
                && b.PaymentDeadlineAt <= nowUtc
                && b.Status == "pending_payment"
                && !b.IsManual
                && b.RecurringGroupId == null
                // Money having arrived should already have disarmed the row; if some future
                // path forgets to, refuse to cancel a booking that has been paid for.
                && b.AmountPaid <= PaymentLedger.Epsilon)
            .OrderBy(b => b.PaymentDeadlineAt)
            .Take(batchSize)
            .Select(b => b.Id)
            .ToListAsync(ct);

        if (candidates.Count == 0) return SweepResult.Nothing;

        // Conditional UPDATE: the full predicate is repeated in the WHERE, so a customer who
        // uploads proof in the milliseconds between the read above and this write simply
        // wins — their row no longer matches and is left alone.
        //
        // This is why the job takes no venue lock. Booking creation serialises on
        // `SELECT * FROM venues WHERE id = ? FOR UPDATE` (BookingsController.cs:425, :1226);
        // a background sweep grabbing those same locks across many venues, in an order the
        // create path does not use, is a deadlock waiting to happen. A compare-and-set on
        // the row itself gets the same correctness with none of that risk — and freeing a
        // slot can never cause a double-booking, only a create that would have failed now
        // succeeding, which is the entire point.
        //
        // ExecuteUpdateAsync also bypasses the change tracker, which keeps this clear of
        // AppDbContext.GuardPaymentLedger — that guard scans the whole ChangeTracker, so one
        // incidentally-tracked Payment would abort the batch.
        var cancelledCount = await _db.Bookings
            .Where(b =>
                candidates.Contains(b.Id)
                && b.PaymentDeadlineAt != null
                && b.PaymentDeadlineAt <= nowUtc
                && b.Status == "pending_payment"
                && !b.IsManual
                && b.RecurringGroupId == null
                && b.AmountPaid <= PaymentLedger.Epsilon)
            .ExecuteUpdateAsync(s => s
                // Verified by substitution: changing this literal to "expired" fails
                // TheFreedSlotCanActuallyBeBookedAgain while every other test still passes —
                // the silent no-op, caught by the one test that checks the slot came back.
                .SetProperty(b => b.Status, "cancelled")
                .SetProperty(b => b.AutoCancelledAt, nowUtc)
                // Disarm. A cancelled booking must never be reconsidered, and leaving a live
                // deadline on a terminal row is a trap for whoever reads this table next.
                .SetProperty(b => b.PaymentDeadlineAt, (DateTime?)null),
                ct);

        if (cancelledCount == 0)
            return new SweepResult(candidates.Count, 0, 0);

        _logger.LogInformation(
            "Booking expiry: released {Cancelled} of {Considered} unpaid holds at {NowUtc:o}",
            cancelledCount, candidates.Count, nowUtc);

        var notified = await NotifyAsync(candidates, nowUtc, ct);
        return new SweepResult(candidates.Count, cancelledCount, notified);
    }

    /// <summary>
    /// Tells each player their hold was released. Runs AFTER the update has committed —
    /// a push is irreversible, so it must never be sent for a change that might roll back.
    /// </summary>
    private async Task<int> NotifyAsync(List<string> candidateIds, DateTime nowUtc, CancellationToken ct)
    {
        // Venue and Player must be eagerly loaded: the notification reads Venue.Name and
        // owner-directed branches elsewhere skip silently when Venue is null, so a bare
        // query would produce plausible-looking output with the venue name missing.
        var victims = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .AsNoTracking()
            .Where(b => candidateIds.Contains(b.Id) && b.AutoCancelledAt == nowUtc)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var booking in victims)
        {
            // Deliberately not passing ct. CreateNotification takes no CancellationToken and
            // its SaveChanges is unguarded; abandoning a half-sent notification during a
            // 30-second shutdown is worse than finishing a handful of small writes.
            try
            {
                await _notifications.NotifyBookingExpired(booking);
                sent++;
            }
            catch (Exception ex)
            {
                // Never let a delivery failure undo a released slot. The booking is already
                // cancelled and committed; the notification is a courtesy on top of it.
                _logger.LogWarning(ex, "Expiry notification failed for booking {BookingId}", booking.Id);
            }
        }

        return sent;
    }
}
