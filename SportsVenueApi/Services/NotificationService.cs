using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services;

public class NotificationService
{
    private readonly AppDbContext _db;

    public NotificationService(AppDbContext db) => _db = db;

    public async Task CreateNotification(string userId, string title, string body, string type, string? referenceId = null)
    {
        var notification = new Notification
        {
            UserId = userId,
            Title = title,
            Body = body,
            Type = type,
            ReferenceId = referenceId,
        };

        _db.Notifications.Add(notification);
        await _db.SaveChangesAsync();

        // TODO: Send FCM push when Firebase is configured
        // For now, notifications are stored in DB and fetched via API polling
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
        // Notify the venue owner
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
        // Notify player if cancelled by owner
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

        // Notify owner if cancelled by player
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
}
