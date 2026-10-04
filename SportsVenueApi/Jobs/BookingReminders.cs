using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.Helpers;
using SportsVenueApi.Services;

namespace SportsVenueApi.Jobs;

public sealed class ReminderOptions
{
    public const string Section = "Jobs:Reminders";

    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 300;
    public int BatchSize { get; set; } = 200;

    /// <summary>How long before a confirmed game the player is reminded.</summary>
    public int GameLeadMinutes { get; set; } = 120;

    /// <summary>How long a payment proof may wait before the venue is nudged.</summary>
    public int ProofWaitMinutes { get; set; } = 30;

    /// <summary>How long before an unpaid booking is released the player is warned.</summary>
    public int PaymentWarningMinutes { get; set; } = 30;
}

public sealed record ReminderResult(int Games, int Proofs, int Payments)
{
    public int Total => Games + Proofs + Payments;
}

/// <summary>
/// One pass of the reminders: a game coming up, a payment proof nobody has looked at, an unpaid
/// booking about to be released. Each goes out once — the notification itself is the record
/// that it was sent, so there is no flag column to keep in step.
/// </summary>
public class BookingReminders
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<BookingReminders> _logger;

    public BookingReminders(AppDbContext db, NotificationService notifications, ILogger<BookingReminders> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    /// <param name="releaseJobOn">
    /// Whether unpaid bookings are actually released. Warning someone that a booking is about to
    /// go, when nothing will take it away, would be a false alarm.
    /// </param>
    public async Task<ReminderResult> RunAsync(
        DateTime nowUtc, ReminderOptions options, bool releaseJobOn, CancellationToken ct = default)
    {
        var games = await GameReminders(nowUtc, options, ct);
        var proofs = await ProofReminders(nowUtc, options, ct);
        var payments = releaseJobOn ? await PaymentWarnings(nowUtc, options, ct) : 0;
        var result = new ReminderResult(games, proofs, payments);
        if (result.Total > 0)
            _logger.LogInformation(
                "Reminders sent: {Games} game, {Proofs} proof, {Payments} payment", games, proofs, payments);
        return result;
    }

    private async Task<int> GameReminders(DateTime nowUtc, ReminderOptions options, CancellationToken ct)
    {
        var lead = TimeSpan.FromMinutes(Math.Max(1, options.GameLeadMinutes));
        // Jordan's date at nowUtc — not the wall clock, so a pass run for a given moment is about that moment.
        var today = nowUtc.AddHours(PlatformConstants.JordanUtcOffsetHours).Date;
        var from = today.AddDays(-1);
        var to = today.AddDays(1);

        var candidates = await _db.Bookings
            .Include(b => b.Venue)
            .Where(b => b.Status == "confirmed" && !b.IsManual && b.Date >= from && b.Date <= to)
            .Where(b => !_db.Notifications.Any(n => n.Type == "game_reminder" && n.ReferenceId == b.Id))
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime)
            .Take(Math.Max(1, options.BatchSize))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var b in candidates)
        {
            var start = PaymentDeadline.SlotStartUtc(b.Date, b.StartTime);
            if (start is null || start <= nowUtc || start.Value - nowUtc > lead) continue;
            // Booked shortly before the game: the booking itself was the reminder.
            if (b.CreatedAt > start.Value - lead - TimeSpan.FromHours(1)) continue;

            await _notifications.NotifyGameReminder(b);
            sent++;
        }
        return sent;
    }

    private async Task<int> ProofReminders(DateTime nowUtc, ReminderOptions options, CancellationToken ct)
    {
        var wait = TimeSpan.FromMinutes(Math.Max(1, options.ProofWaitMinutes));
        var cutoff = nowUtc - wait;

        // When the proof arrived is when the venue was told about it — the latest such notice,
        // so a proof sent again after a rejection starts the clock again.
        var waiting = await _db.Bookings
            .Where(b => b.Status == "pending_review")
            .Select(b => new
            {
                b.Id,
                UploadedAt = _db.Notifications
                    .Where(n => n.Type == "proof_received" && n.ReferenceId == b.Id)
                    .Max(n => (DateTime?)n.CreatedAt),
            })
            .Where(x => x.UploadedAt != null && x.UploadedAt <= cutoff)
            .Where(x => !_db.Notifications.Any(n =>
                n.Type == "proof_waiting" && n.ReferenceId == x.Id && n.CreatedAt >= x.UploadedAt))
            .Take(Math.Max(1, options.BatchSize))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var w in waiting)
        {
            var booking = await _db.Bookings
                .Include(b => b.Venue)
                .Include(b => b.Player)
                .AsSplitQuery()
                .FirstOrDefaultAsync(b => b.Id == w.Id, ct);
            if (booking == null) continue;

            var minutes = (int)Math.Round((nowUtc - w.UploadedAt!.Value).TotalMinutes);
            await _notifications.NotifyProofWaiting(booking, minutes);
            sent++;
        }
        return sent;
    }

    private async Task<int> PaymentWarnings(DateTime nowUtc, ReminderOptions options, CancellationToken ct)
    {
        var warning = TimeSpan.FromMinutes(Math.Max(1, options.PaymentWarningMinutes));
        var soon = nowUtc + warning;

        // The same rows the release job will take: an app booking, not part of a series,
        // waiting on payment with a deadline armed.
        var candidates = await _db.Bookings
            .Include(b => b.Venue)
            .Where(b => b.Status == "pending_payment" && !b.IsManual && b.RecurringGroupId == null
                && b.PaymentDeadlineAt != null && b.PaymentDeadlineAt > nowUtc && b.PaymentDeadlineAt <= soon)
            .Where(b => !_db.Notifications.Any(n => n.Type == "payment_deadline" && n.ReferenceId == b.Id))
            .Take(Math.Max(1, options.BatchSize))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var b in candidates)
        {
            var deadline = b.PaymentDeadlineAt!.Value;
            // A short window (a game starting soon) leaves no room for a warning worth sending.
            if (deadline - b.CreatedAt < warning * 2) continue;

            var minutesLeft = (int)Math.Ceiling((deadline - nowUtc).TotalMinutes);
            await _notifications.NotifyPaymentDeadline(b, minutesLeft);
            sent++;
        }
        return sent;
    }
}

public class ReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ReminderOptions _options;
    private readonly bool _releaseJobOn;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(
        IServiceScopeFactory scopes,
        IOptions<ReminderOptions> options,
        IOptions<BookingExpiryOptions> expiry,
        ILogger<ReminderService> logger)
    {
        _scopes = scopes;
        _options = options.Value;
        _releaseJobOn = expiry.Value.Enabled;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.IntervalSeconds));
        _logger.LogInformation("Reminders job started: every {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var reminders = scope.ServiceProvider.GetRequiredService<BookingReminders>();
                await reminders.RunAsync(DateTime.UtcNow, _options, _releaseJobOn, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Same rule as the expiry job: nothing escapes the loop, or one bad tick stops the API.
                _logger.LogError(ex, "Reminders pass failed; will retry next tick");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Reminders job stopped");
    }
}
