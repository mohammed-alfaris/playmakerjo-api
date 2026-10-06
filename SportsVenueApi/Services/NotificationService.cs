using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services;

public class NotificationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(AppDbContext db, ILogger<NotificationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task CreateNotification(string userId, string title, string body, string type, string? referenceId = null, string? image = null)
    {
        var notification = new Models.Notification
        {
            UserId = userId,
            Title = title,
            Body = body,
            Type = type,
            ReferenceId = referenceId,
        };

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();

        // Send FCM push notification
        await SendPushNotification(userId, title, body, type, referenceId, image);
    }

    /// <summary>
    /// Send push notification to all active devices of a user via FCM.
    /// Uses the user's preferred language from the database.
    /// </summary>
    private async Task SendPushNotification(string userId, string title, string body, string type, string? referenceId, string? image = null)
    {
        try
        {
            // Get user's preferred language
            var userLang = await _db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.PreferredLanguage)
                .FirstOrDefaultAsync() ?? "en";

            var tokens = await _db.DeviceTokens
                .Where(d => d.UserId == userId && d.IsActive)
                .Select(d => d.Token)
                .ToListAsync();

            if (tokens.Count == 0) return;

            var message = new MulticastMessage
            {
                Tokens = tokens,
                Notification = new FirebaseAdmin.Messaging.Notification
                {
                    Title = Localized(title, userLang),
                    Body = Localized(body, userLang),
                    ImageUrl = image,
                },
                Data = new Dictionary<string, string>
                {
                    ["type"] = type,
                    ["referenceId"] = referenceId ?? "",
                },
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        ChannelId = "playmakerjo_bookings",
                        Sound = "default",
                    },
                },
                Apns = new ApnsConfig
                {
                    Aps = new Aps
                    {
                        Sound = "default",
                        Badge = 1,
                    },
                },
            };

            var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message);

            // Deactivate tokens that failed (e.g., uninstalled app)
            if (response.FailureCount > 0)
            {
                for (int i = 0; i < response.Responses.Count; i++)
                {
                    if (!response.Responses[i].IsSuccess)
                    {
                        var failedToken = tokens[i];
                        var errorCode = response.Responses[i].Exception?.MessagingErrorCode;

                        if (errorCode == MessagingErrorCode.Unregistered ||
                            errorCode == MessagingErrorCode.InvalidArgument)
                        {
                            await _db.DeviceTokens
                                .Where(d => d.Token == failedToken)
                                .ExecuteUpdateAsync(d => d.SetProperty(x => x.IsActive, false));

                            _logger.LogInformation("Deactivated stale FCM token for user {UserId}", userId);
                        }
                    }
                }
            }

            _logger.LogInformation(
                "FCM sent to {UserId}: {Success} success, {Failure} failed",
                userId, response.SuccessCount, response.FailureCount);
        }
        catch (Exception ex)
        {
            // Don't let FCM failures break the main flow
            _logger.LogWarning(ex, "Failed to send FCM push to user {UserId}", userId);
        }
    }

    // ── Bilingual helpers ──────────────────────────────────────────────
    // Format: "English|العربية" — the app splits on "|" and picks by locale.
    private static string Bi(string en, string ar) => $"{en}|{ar}";

    /// <summary>
    /// Extract the correct language part from a bilingual "en|ar" string.
    /// </summary>
    private static string Localized(string bilingualText, string lang)
    {
        var idx = bilingualText.IndexOf('|');
        if (idx < 0) return bilingualText;
        return lang == "ar" ? bilingualText[(idx + 1)..] : bilingualText[..idx];
    }

    /// <summary>
    /// Who on the venue's side hears about something: the owner, and every active clerk of
    /// theirs whose role includes <paramref name="permission"/> and who works at this venue.
    /// <paramref name="except"/> — whoever did the thing — is left out; they already know.
    /// </summary>
    public async Task<List<string>> VenueTeam(Venue venue, string permission, string? except = null)
    {
        var staff = await _db.Users.AsNoTracking()
            .Include(u => u.StaffRole)
            .Where(u => u.Role == "venue_staff" && u.Status == "active" && u.ManagedByOwnerId == venue.OwnerId)
            .ToListAsync();

        var ids = new List<string> { venue.OwnerId };
        ids.AddRange(staff
            .Where(s => s.StaffAllVenues || s.StaffVenueIds.Contains(venue.Id))
            .Where(s => StaffPermissions.For(s, venue.OwnerId).Contains(permission))
            .Select(s => s.Id));
        return ids.Where(id => id != except).Distinct().ToList();
    }

    /// <summary>
    /// The person behind a booking, as the venue knows them: the player for an app booking,
    /// the customer for a counter or web one (whose PlayerId is the owner's own id).
    /// </summary>
    private static string WhoBooked(Booking b, string fallback) =>
        (b.IsManual ? b.Customer?.Name : b.Player?.Name) ?? fallback;

    private async Task NotifyVenueTeam(
        Venue venue, string permission, string? except, string title, string body, string type, string? referenceId)
    {
        foreach (var userId in await VenueTeam(venue, permission, except))
            await CreateNotification(userId, title, body, type, referenceId);
    }

    /// <summary>
    /// A player booked through the app. The owner hears about it the moment it happens — the
    /// dashboard's inbox shows it and refreshes the schedule. Counter bookings do not come
    /// here: whoever keyed one in is standing at the desk already.
    /// </summary>
    public async Task NotifyNewBooking(Booking booking)
    {
        if (booking.Venue == null) return;
        var player = WhoBooked(booking, "A player");
        var when = $"{booking.Date:yyyy-MM-dd} {booking.StartTime}";
        await NotifyVenueTeam(
            booking.Venue, StaffPermissions.BookingsView, null,
            Bi("New booking", "حجز جديد"),
            Bi(
                $"{player} booked {booking.Venue.Name} for {when}.",
                $"{player} حجز {booking.Venue.Name} بتاريخ {when}."
            ),
            "new_booking",
            booking.Id
        );
    }

    /// <summary>
    /// The venue moved an app booking. The player is told where it went — they would otherwise
    /// turn up at the old time. Only the player: the venue made the change.
    /// </summary>
    public async Task NotifyBookingMoved(Booking booking)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "venue";
        var when = $"{booking.Date:yyyy-MM-dd} {booking.StartTime}";
        await CreateNotification(
            booking.PlayerId,
            Bi("Booking moved", "تم تغيير موعد الحجز"),
            Bi(
                $"Your booking at {venue} is now on {when}.",
                $"حجزك في {venue} أصبح بتاريخ {when}."
            ),
            "booking_moved",
            booking.Id
        );
    }

    /// <summary>PlayMaker issued the company an invoice. The owner only — billing is theirs.</summary>
    public async Task NotifyInvoiceIssued(Invoice invoice)
    {
        var total = invoice.Total.ToString("0.###");
        await CreateNotification(
            invoice.OwnerId,
            Bi("New invoice", "فاتورة جديدة"),
            Bi(
                $"Invoice {invoice.Number} for {invoice.Period}: {total} JOD, due {invoice.DueOn:yyyy-MM-dd}.",
                $"الفاتورة {invoice.Number} عن {invoice.Period}: {total} د.أ، تستحق بتاريخ {invoice.DueOn:yyyy-MM-dd}."
            ),
            "invoice_issued",
            invoice.Id
        );
    }

    /// <summary>A weekly series booked through the app: one notice for the whole series, not one per week.</summary>
    public async Task NotifyNewSeries(Booking first, int sessions)
    {
        if (first.Venue == null) return;
        var player = first.Player?.Name ?? "A player";
        var when = $"{first.Date:yyyy-MM-dd} {first.StartTime}";
        await NotifyVenueTeam(
            first.Venue, StaffPermissions.BookingsView, null,
            Bi("New weekly booking", "حجز أسبوعي جديد"),
            Bi(
                $"{player} booked {first.Venue.Name} for {sessions} sessions, starting {when}.",
                $"{player} حجز {first.Venue.Name} لـ {sessions} جلسات، تبدأ {when}."
            ),
            "new_series",
            first.Id
        );
    }

    public async Task NotifyBookingConfirmed(Booking booking)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "venue";
        var date = booking.Date.ToString("MMM dd");
        await CreateNotification(
            booking.PlayerId,
            Bi("Booking Confirmed", "تم تأكيد الحجز"),
            Bi(
                $"Your booking at {venue} on {date} has been confirmed.",
                $"تم تأكيد حجزك في {venue} بتاريخ {date}."
            ),
            "booking_confirmed",
            booking.Id
        );
    }

    /// <summary>
    /// The unpaid booking held a slot until its deadline and has been released.
    ///
    /// Deliberately NOT reusing NotifyBookingCancelled: that method notifies the player AND
    /// the venue owner, suppressing whichever one performed the cancellation by comparing
    /// against an actor id. A background job has no actor id, so it would notify both — and
    /// on a manual booking, where PlayerId holds the owner's own id, that means telling the
    /// same person twice about a booking they never made.
    ///
    /// This one reaches the player only. The owner finds out by looking at a schedule with
    /// a free slot in it, which is the outcome he wanted; a push at 2am about money that
    /// never arrived is noise.
    ///
    /// The type string stays "booking_cancelled" on purpose. It is what the mobile client
    /// routes on, it is free-form with no validation, and from the player's side this IS a
    /// cancellation — a new string would just be an unhandled case in the app.
    /// </summary>
    public async Task NotifyBookingExpired(Booking booking)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "the venue";
        var date = booking.Date.ToString("MMM dd");
        var time = booking.StartTime ?? "";

        await CreateNotification(
            booking.PlayerId,
            Bi("Booking Released", "تم إلغاء الحجز"),
            Bi(
                $"Your unpaid booking at {venue} on {date} {time} has been released. You can book again any time.",
                $"تم إلغاء حجزك غير المدفوع في {venue} بتاريخ {date} {time}. تقدر تحجز من جديد بأي وقت."
            ),
            "booking_cancelled",
            booking.Id
        );
    }

    /// <summary>
    /// A venue owner filled in the website's sign-up form: that is a sales lead, and it goes to
    /// every active admin so nobody has to remember to check the Leads page.
    /// </summary>
    public async Task NotifyNewVenueLead(VenueWaitlist lead)
    {
        var admins = await _db.Users
            .Where(u => u.Role == "super_admin" && u.Status == "active")
            .Select(u => u.Id)
            .ToListAsync();
        foreach (var adminId in admins)
        {
            await CreateNotification(
                adminId,
                Bi("New venue lead", "طلب انضمام ملعب جديد"),
                Bi(
                    $"{lead.VenueName} ({lead.City}) — {lead.ContactName}, {lead.Phone}",
                    $"{lead.VenueName} ({lead.City}) — {lead.ContactName}، {lead.Phone}"
                ),
                "venue_lead",
                lead.Id.ToString()
            );
        }
    }

    public async Task NotifyProofReceived(Booking booking)
    {
        if (booking.Venue != null)
        {
            var player = WhoBooked(booking, "a player");
            await NotifyVenueTeam(
                booking.Venue, StaffPermissions.PaymentsRecord, null,
                Bi("Payment Proof Received", "تم استلام إثبات الدفع"),
                Bi(
                    $"A payment proof has been uploaded for booking at {booking.Venue.Name} by {player}.",
                    $"تم رفع إثبات دفع لحجز في {booking.Venue.Name} من قبل {player}."
                ),
                "proof_received",
                booking.Id
            );
        }
    }

    public async Task NotifyProofApproved(Booking booking)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "venue";
        await CreateNotification(
            booking.PlayerId,
            Bi("Payment Approved", "تمت الموافقة على الدفع"),
            Bi(
                $"Your payment proof for {venue} has been approved. Your booking is confirmed!",
                $"تمت الموافقة على إثبات الدفع لـ {venue}. تم تأكيد حجزك!"
            ),
            "proof_approved",
            booking.Id
        );
    }

    public async Task NotifyProofRejected(Booking booking, string? reason)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "venue";
        var enBody = $"Your payment proof for {venue} was rejected.";
        var arBody = $"تم رفض إثبات الدفع لـ {venue}.";
        if (!string.IsNullOrEmpty(reason))
        {
            enBody += $" Reason: {reason}";
            arBody += $" السبب: {reason}";
        }

        await CreateNotification(
            booking.PlayerId,
            Bi("Payment Rejected", "تم رفض الدفع"),
            Bi(enBody, arBody),
            "proof_rejected",
            booking.Id
        );
    }

    public async Task NotifyBookingCancelled(Booking booking, string cancelledByUserId)
    {
        var venue = booking.Venue?.Name ?? "venue";
        var date = booking.Date.ToString("MMM dd");
        var player = WhoBooked(booking, "a player");

        // A counter booking's PlayerId is the owner's own id, not a player: nobody to tell,
        // and the desk that took it is the desk that cancelled it. A web booking has no player
        // to tell either, but the team should hear when the guest or a colleague cancels it.
        if (booking.IsManual && !booking.IsWeb) return;

        if (!booking.IsManual && cancelledByUserId != booking.PlayerId)
        {
            await CreateNotification(
                booking.PlayerId,
                Bi("Booking Cancelled", "تم إلغاء الحجز"),
                Bi(
                    $"Your booking at {venue} on {date} has been cancelled.",
                    $"تم إلغاء حجزك في {venue} بتاريخ {date}."
                ),
                "booking_cancelled",
                booking.Id
            );
        }

        if (booking.Venue != null)
        {
            await NotifyVenueTeam(
                booking.Venue, StaffPermissions.BookingsView, cancelledByUserId,
                Bi("Booking Cancelled", "تم إلغاء الحجز"),
                Bi(
                    $"A booking at {booking.Venue.Name} by {player} on {date} has been cancelled.",
                    $"تم إلغاء حجز في {booking.Venue.Name} من قبل {player} بتاريخ {date}."
                ),
                "booking_cancelled",
                booking.Id
            );
        }
    }

    public async Task NotifyBookingCompleted(Booking booking)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "venue";
        var date = booking.Date.ToString("MMM dd");
        await CreateNotification(
            booking.PlayerId,
            Bi("Booking Completed", "اكتمل الحجز"),
            Bi(
                $"Your booking at {venue} on {date} has been marked as completed.",
                $"تم تحديد حجزك في {venue} بتاريخ {date} كمكتمل."
            ),
            "booking_completed",
            booking.Id
        );
    }

    public async Task NotifyNoShow(Booking booking)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "venue";
        var date = booking.Date.ToString("MMM dd");
        await CreateNotification(
            booking.PlayerId,
            Bi("No Show", "لم يحضر"),
            Bi(
                $"You were marked as a no-show for your booking at {venue} on {date}.",
                $"تم تسجيلك كغائب عن حجزك في {venue} بتاريخ {date}."
            ),
            "no_show",
            booking.Id
        );
    }

    /// <summary>
    /// A weekly series was cancelled. The player hears unless they did it; the venue team hears
    /// unless one of them did it. One notice for the series, not one per week.
    /// </summary>
    public async Task NotifySeriesCancelled(RecurringBookingGroup group, int sessions, string cancelledByUserId)
    {
        if (sessions == 0 || group.Venue == null) return;
        var venue = group.Venue.Name;
        var player = await _db.Users.Where(u => u.Id == group.PlayerId).Select(u => u.Name).FirstOrDefaultAsync() ?? "A player";

        if (cancelledByUserId != group.PlayerId)
        {
            await CreateNotification(
                group.PlayerId,
                Bi("Weekly booking cancelled", "تم إلغاء الحجز الأسبوعي"),
                Bi(
                    $"Your weekly booking at {venue} was cancelled: {sessions} upcoming session(s).",
                    $"تم إلغاء حجزك الأسبوعي في {venue}: {sessions} جلسة قادمة."
                ),
                "series_cancelled",
                group.Id
            );
        }

        await NotifyVenueTeam(
            group.Venue, StaffPermissions.BookingsView, cancelledByUserId,
            Bi("Weekly booking cancelled", "تم إلغاء الحجز الأسبوعي"),
            Bi(
                $"{player}'s weekly booking at {venue} was cancelled: {sessions} upcoming session(s).",
                $"تم إلغاء الحجز الأسبوعي لـ {player} في {venue}: {sessions} جلسة قادمة."
            ),
            "series_cancelled",
            group.Id
        );
    }

    /// <summary>
    /// A guest booked on the venue's web link. They have no account, so the team is the one to
    /// act: confirm or decline a request, or watch for the deposit when they chose to pay now.
    /// </summary>
    public async Task NotifyWebRequest(Booking booking)
    {
        if (booking.Venue == null) return;
        var who = booking.Customer?.Name ?? "A guest";
        var phone = booking.Customer?.Phone ?? "";
        var when = $"{booking.Date:yyyy-MM-dd} {booking.StartTime}";
        var paying = booking.Status == "pending_payment";
        await NotifyVenueTeam(
            booking.Venue, StaffPermissions.BookingsView, null,
            Bi(paying ? "Web booking (paying now)" : "Web booking request", paying ? "حجز من الويب (دفع الآن)" : "طلب حجز من الويب"),
            Bi(
                paying
                    ? $"{who} ({phone}) is holding {booking.Venue.Name} for {when} and paying the deposit by CliQ."
                    : $"{who} ({phone}) asks to book {booking.Venue.Name} for {when}. Confirm or decline.",
                paying
                    ? $"{who} ({phone}) حجز {booking.Venue.Name} بتاريخ {when} وسيدفع العربون عبر كليك."
                    : $"{who} ({phone}) يطلب حجز {booking.Venue.Name} بتاريخ {when}. أكّد أو ارفض."
            ),
            "web_request",
            booking.Id
        );
    }

    /// <summary>A player reviewed the venue. Those who follow how the venue is doing hear about it.</summary>
    public async Task NotifyNewReview(Venue venue, string playerName, int rating)
    {
        await NotifyVenueTeam(
            venue, StaffPermissions.ReportsView, null,
            Bi("New review", "تقييم جديد"),
            Bi(
                $"{playerName} rated {venue.Name} {rating}/5.",
                $"{playerName} قيّم {venue.Name} بـ {rating}/5."
            ),
            "new_review",
            venue.Id
        );
    }

    // ── Reminders (sent by the reminders job, once each) ──────────────────

    /// <summary>The player's confirmed game is coming up.</summary>
    public async Task NotifyGameReminder(Booking booking)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "the venue";
        await CreateNotification(
            booking.PlayerId,
            Bi("Your game is soon", "مباراتك قريبة"),
            Bi(
                $"Your game at {venue} starts at {booking.StartTime} on {booking.Date:yyyy-MM-dd}.",
                $"مباراتك في {venue} تبدأ الساعة {booking.StartTime} بتاريخ {booking.Date:yyyy-MM-dd}."
            ),
            "game_reminder",
            booking.Id
        );
    }

    /// <summary>A payment proof has waited a while for someone at the venue to look at it.</summary>
    public async Task NotifyProofWaiting(Booking booking, int minutes)
    {
        if (booking.Venue == null) return;
        var player = WhoBooked(booking, "A player");
        var when = $"{booking.Date:yyyy-MM-dd} {booking.StartTime}";
        await NotifyVenueTeam(
            booking.Venue, StaffPermissions.PaymentsRecord, null,
            Bi("Payment proof waiting", "إثبات دفع بانتظار المراجعة"),
            Bi(
                $"{player}'s payment proof for {booking.Venue.Name} on {when} has been waiting {minutes} minutes.",
                $"إثبات دفع {player} لحجز {booking.Venue.Name} بتاريخ {when} بانتظار المراجعة منذ {minutes} دقيقة."
            ),
            "proof_waiting",
            booking.Id
        );
    }

    /// <summary>The player's unpaid booking is about to be released.</summary>
    public async Task NotifyPaymentDeadline(Booking booking, int minutesLeft)
    {
        if (booking.IsManual) return;   // no player account (counter or web): nobody to tell
        var venue = booking.Venue?.Name ?? "the venue";
        var when = $"{booking.Date:yyyy-MM-dd} {booking.StartTime}";
        await CreateNotification(
            booking.PlayerId,
            Bi("Pay to keep your booking", "ادفع للحفاظ على حجزك"),
            Bi(
                $"Your booking at {venue} on {when} will be released in {minutesLeft} minutes unless you upload your payment proof.",
                $"سيتم إلغاء حجزك في {venue} بتاريخ {when} خلال {minutesLeft} دقيقة إذا لم ترفع إثبات الدفع."
            ),
            "payment_deadline",
            booking.Id
        );
    }
}
