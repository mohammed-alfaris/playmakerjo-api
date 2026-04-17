using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
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

    public async Task NotifyBookingConfirmed(Booking booking)
    {
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

    public async Task NotifyProofReceived(Booking booking)
    {
        if (booking.Venue != null)
        {
            var player = booking.Player?.Name ?? "a player";
            await CreateNotification(
                booking.Venue.OwnerId,
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
        var player = booking.Player?.Name ?? "a player";

        if (cancelledByUserId != booking.PlayerId)
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

        if (booking.Venue != null && cancelledByUserId != booking.Venue.OwnerId)
        {
            await CreateNotification(
                booking.Venue.OwnerId,
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
}
