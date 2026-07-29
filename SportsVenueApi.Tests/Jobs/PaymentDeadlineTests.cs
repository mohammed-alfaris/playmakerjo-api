using SportsVenueApi.Constants;
using SportsVenueApi.Helpers;

namespace SportsVenueApi.Tests.Jobs;

/// <summary>
/// The deadline arithmetic, tested without a database or a clock.
///
/// This is where the two failure modes of a fixed expiry window live, and they pull in
/// opposite directions: too generous and the job never frees a slot before its own kick-off;
/// too eager and it cancels a booking seconds after somebody made it.
/// </summary>
public class PaymentDeadlineTests
{
    private static readonly ExpiryPolicy Policy = new(WindowMinutes: 120, SlotBufferMinutes: 30, MinimumMinutes: 10);

    /// <summary>Builds a UTC "now" and a Jordan-local slot a given number of minutes ahead of it.</summary>
    private static (DateTime NowUtc, DateTime Date, string Start) SlotIn(int minutesAhead)
    {
        var nowUtc = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
        var slotUtc = nowUtc.AddMinutes(minutesAhead);
        var jordan = slotUtc.AddHours(PlatformConstants.JordanUtcOffsetHours);
        return (nowUtc, jordan.Date, jordan.ToString("HH:mm"));
    }

    [Fact]
    public void DistantSlot_GetsTheFullWindow()
    {
        var (now, date, start) = SlotIn(minutesAhead: 60 * 24 * 5);

        Assert.Equal(now.AddMinutes(120), PaymentDeadline.Compute(now, date, start, Policy));
    }

    [Fact]
    public void SlotTonight_IsReleasedBeforeKickOff_NotAfterIt()
    {
        // The case a fixed window gets exactly backwards. For the slot term to bind, the
        // slot has to be nearer than window + buffer (150 min here) — beyond that the plain
        // window is already the earlier of the two and there is nothing to clamp.
        var (now, date, start) = SlotIn(minutesAhead: 100);

        var deadline = PaymentDeadline.Compute(now, date, start, Policy);

        Assert.Equal(now.AddMinutes(70), deadline);           // 30 min before kick-off
        Assert.True(deadline < now.AddMinutes(100));          // and strictly before it
        Assert.True(deadline < now.AddMinutes(Policy.WindowMinutes), "the window alone would release it too late");
    }

    [Fact]
    public void SlotFurtherOutThanWindowPlusBuffer_IsGovernedByTheWindow()
    {
        var (now, date, start) = SlotIn(minutesAhead: 300);

        Assert.Equal(now.AddMinutes(120), PaymentDeadline.Compute(now, date, start, Policy));
    }

    [Fact]
    public void BookingMadeMinutesBeforeKickOff_IsNotBornAlreadyExpired()
    {
        // Without the floor this returns a deadline 25 minutes in the PAST, and the next
        // sweep cancels a booking the customer made seconds ago.
        var (now, date, start) = SlotIn(minutesAhead: 5);

        var deadline = PaymentDeadline.Compute(now, date, start, Policy);

        Assert.True(deadline > now, "a freshly created booking must never already be expired");
        Assert.Equal(now.AddMinutes(10), deadline);
    }

    [Fact]
    public void SlotAlreadyStarted_StillGivesTheFloor()
    {
        var (now, date, start) = SlotIn(minutesAhead: -60);

        Assert.Equal(now.AddMinutes(10), PaymentDeadline.Compute(now, date, start, Policy));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a time")]
    [InlineData("25:00")]
    public void UnusableStartTime_FallsBackToTheWindow(string? startTime)
    {
        // Both conflict scans already tolerate a null StartTime rather than throwing
        // (BookingsController.cs:438, :1239); this must not be the one place that does.
        var now = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);

        var deadline = PaymentDeadline.Compute(now, new DateTime(2026, 8, 6), startTime, Policy);

        Assert.Equal(now.AddMinutes(120), deadline);
    }

    [Fact]
    public void SlotStart_IsInterpretedAsJordanLocal_NotUtc()
    {
        // Date and StartTime are both naive Jordan-local (Booking.cs:48-53). Reading them as
        // UTC would move every deadline by three hours — enough to release evening slots
        // while the game is being played.
        var slot = PaymentDeadline.SlotStartUtc(new DateTime(2026, 8, 4), "18:00");

        Assert.Equal(new DateTime(2026, 8, 4, 15, 0, 0), slot);
    }

    [Fact]
    public void SlotStart_HandlesMidnightAndMissingTime()
    {
        Assert.Equal(new DateTime(2026, 8, 3, 21, 0, 0), PaymentDeadline.SlotStartUtc(new DateTime(2026, 8, 4), "00:00"));
        Assert.Null(PaymentDeadline.SlotStartUtc(new DateTime(2026, 8, 4), null));
    }
}
