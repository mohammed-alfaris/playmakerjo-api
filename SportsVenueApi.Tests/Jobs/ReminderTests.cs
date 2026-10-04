using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.Helpers;
using SportsVenueApi.Jobs;
using SportsVenueApi.Models;
using SportsVenueApi.Services;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Jobs;

/// <summary>
/// The reminders job: each reminder goes out at the right moment, and only once. Time is
/// simulated by handing RunAsync the moment to act for, as the expiry job's tests do.
/// </summary>
[Collection("Api")]
public class ReminderTests
{
    private readonly DatabaseFixture _fx;

    public ReminderTests(DatabaseFixture fx) => _fx = fx;

    private static readonly ReminderOptions Options = new();

    private async Task<ReminderResult> Run(DateTime nowUtc, bool releaseJobOn = true)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BookingReminders>().RunAsync(nowUtc, Options, releaseJobOn);
    }

    private async Task<int> Count(string userId, string type, string refId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.Type == type && n.ReferenceId == refId);
    }

    private async Task<Booking> Book(string venueId, string playerId, string status, int days, Action<Booking>? mutate = null)
    {
        var b = new Booking
        {
            VenueId = venueId, PlayerId = playerId, Sport = "basketball",
            Date = PlatformConstants.JordanToday().AddDays(days), StartTime = "18:00", Duration = 60,
            Amount = 20, TotalAmount = 20, DepositAmount = 4, Status = status,
            CreatedAt = DateTime.UtcNow,
        };
        mutate?.Invoke(b);
        return await _fx.Insert(b);
    }

    private static DateTime StartOf(Booking b) => PaymentDeadline.SlotStartUtc(b.Date, b.StartTime)!.Value;

    [Fact]
    public async Task AConfirmedGame_IsRemindedOnce_InTheHoursBeforeIt()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();
        var booking = await Book(venue.Id, player.Id, "confirmed", days: 5);
        var start = StartOf(booking);

        await Run(start.AddHours(-5));   // too early
        Assert.Equal(0, await Count(player.Id, "game_reminder", booking.Id));

        await Run(start.AddMinutes(-90));
        await Run(start.AddMinutes(-60));
        Assert.Equal(1, await Count(player.Id, "game_reminder", booking.Id));
    }

    [Fact]
    public async Task AGameBookedJustBeforeItStarts_GetsNoReminder()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();
        var booking = await Book(venue.Id, player.Id, "confirmed", days: 5,
            b => b.CreatedAt = PaymentDeadline.SlotStartUtc(b.Date, b.StartTime)!.Value.AddHours(-2));

        await Run(StartOf(booking).AddMinutes(-90));

        Assert.Equal(0, await Count(player.Id, "game_reminder", booking.Id));
    }

    [Fact]
    public async Task ACounterBooking_GetsNoGameReminder()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var booking = await Book(venue.Id, owner.Id, "confirmed", days: 5, b => b.IsManual = true);

        await Run(StartOf(booking).AddMinutes(-90));

        Assert.Equal(0, await Count(owner.Id, "game_reminder", booking.Id));
    }

    [Fact]
    public async Task AProofWaitingHalfAnHour_NudgesThePaymentsTeamOnce()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var cashier = await _fx.CreateStaff(owner.Id, "write");   // the legacy write level records payments
        var player = await _fx.CreatePlayer();
        var booking = await Book(venue.Id, player.Id, "pending_review", days: 5, b => b.PaymentProofStatus = "pending_review");

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var loaded = await db.Bookings.Include(b => b.Venue).Include(b => b.Player).FirstAsync(b => b.Id == booking.Id);
            await scope.ServiceProvider.GetRequiredService<NotificationService>().NotifyProofReceived(loaded);
        }

        var now = DateTime.UtcNow;
        await Run(now.AddMinutes(10));   // not yet
        Assert.Equal(0, await Count(owner.Id, "proof_waiting", booking.Id));

        await Run(now.AddMinutes(35));
        await Run(now.AddMinutes(50));
        Assert.Equal(1, await Count(owner.Id, "proof_waiting", booking.Id));
        Assert.Equal(1, await Count(cashier.Id, "proof_waiting", booking.Id));
    }

    [Fact]
    public async Task AnUnpaidBooking_IsWarnedOnceBeforeItIsReleased_OnlyWhenReleasesHappen()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();
        var now = DateTime.UtcNow;
        var booking = await Book(venue.Id, player.Id, "pending_payment", days: 5, b =>
        {
            b.CreatedAt = now;
            b.PaymentDeadlineAt = now.AddMinutes(120);
        });

        await Run(now.AddMinutes(100), releaseJobOn: false);
        Assert.Equal(0, await Count(player.Id, "payment_deadline", booking.Id));

        await Run(now.AddMinutes(60));   // more than 30 minutes left
        Assert.Equal(0, await Count(player.Id, "payment_deadline", booking.Id));

        await Run(now.AddMinutes(100));
        await Run(now.AddMinutes(110));
        Assert.Equal(1, await Count(player.Id, "payment_deadline", booking.Id));
    }
}
