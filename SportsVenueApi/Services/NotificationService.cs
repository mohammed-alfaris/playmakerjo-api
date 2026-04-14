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
    /// </summary>
    private async Task SendPushNotification(string userId, string title, string body, string type, string? referenceId, string? image = null)
    {
        try
        {
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
                    Title = title,
                    Body = body,
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
                        ChannelId = "yallanhjez_bookings",
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

    public async Task NotifyBookingConfirmed(Booking booking)
    {
        await CreateNotification(
            booking.PlayerId,
            "Booking Confirmed",
            $"Your booking at {booking.Venue?.Name ?? "venue"} on {booking.Date:MMM dd} has been confirmed.",
            "booking_confirmed",
            booking.Id
        );
    }

    public async Task NotifyProofReceived(Booking booking)
    {
        if (booking.Venue != null)
        {
            await CreateNotification(
                booking.Venue.OwnerId,
                "Payment Proof Received",
                $"A payment proof has been uploaded for booking at {booking.Venue.Name} by {booking.Player?.Name ?? "a player"}.",
                "proof_received",
                booking.Id
            );
        }
    }

    public async Task NotifyProofApproved(Booking booking)
    {
        await CreateNotification(
            booking.PlayerId,
            "Payment Approved",
            $"Your payment proof for {booking.Venue?.Name ?? "venue"} has been approved. Your booking is confirmed!",
            "proof_approved",
            booking.Id
        );
    }

    public async Task NotifyProofRejected(Booking booking, string? reason)
    {
        var body = $"Your payment proof for {booking.Venue?.Name ?? "venue"} was rejected.";
        if (!string.IsNullOrEmpty(reason))
            body += $" Reason: {reason}";

        await CreateNotification(
            booking.PlayerId,
            "Payment Rejected",
            body,
            "proof_rejected",
            booking.Id
        );
    }

    public async Task NotifyBookingCancelled(Booking booking, string cancelledByUserId)
    {
        if (cancelledByUserId != booking.PlayerId)
        {
            await CreateNotification(
                booking.PlayerId,
                "Booking Cancelled",
                $"Your booking at {booking.Venue?.Name ?? "venue"} on {booking.Date:MMM dd} has been cancelled.",
                "booking_cancelled",
                booking.Id
            );
        }

        if (booking.Venue != null && cancelledByUserId != booking.Venue.OwnerId)
        {
            await CreateNotification(
                booking.Venue.OwnerId,
                "Booking Cancelled",
                $"A booking at {booking.Venue.Name} by {booking.Player?.Name ?? "a player"} on {booking.Date:MMM dd} has been cancelled.",
                "booking_cancelled",
                booking.Id
            );
        }
    }

    public async Task NotifyBookingCompleted(Booking booking)
    {
        await CreateNotification(
            booking.PlayerId,
            "Booking Completed",
            $"Your booking at {booking.Venue?.Name ?? "venue"} on {booking.Date:MMM dd} has been marked as completed.",
            "booking_completed",
            booking.Id
        );
    }

    public async Task NotifyNoShow(Booking booking)
    {
        await CreateNotification(
            booking.PlayerId,
            "No Show",
            $"You were marked as a no-show for your booking at {booking.Venue?.Name ?? "venue"} on {booking.Date:MMM dd}.",
            "no_show",
            booking.Id
        );
    }
}
