using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Money;

/// <summary>
/// PATCH /bookings/{id}: moving a booking to another time, length, pitch or size, and
/// re-pricing it. The new slot passes the same checks as a new booking, with the booking
/// itself left out of the conflict scan.
/// </summary>
[Collection("Api")]
public class MoveBookingTests
{
    private readonly DatabaseFixture _fx;

    public MoveBookingTests(DatabaseFixture fx) => _fx = fx;

    private static string Day(int ahead) => PlatformConstants.JordanToday().AddDays(ahead).ToString("yyyy-MM-dd");

    private sealed record Co(User Owner, HttpClient Client, Venue Venue);

    private async Task<Co> NewCompany()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        return new Co(owner, _fx.CreateClientFor(owner.Id, "venue_owner"), venue);
    }

    /// <summary>A counter booking at 20 JOD/h.</summary>
    private static async Task<string> Counter(Co co, string start, int duration = 60, bool paid = false, int daysAhead = 4)
    {
        var res = await co.Client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = co.Venue.Id, sport = "basketball", date = Day(daysAhead), startTime = start, duration,
            paymentMethod = "cliq", isManual = true, customerPaid = paid,
            customerPhone = "0791234" + Random.Shared.Next(100, 999), customerName = "Move Test",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
    }

    private static Task<HttpResponseMessage> Patch(HttpClient client, string id, object body) =>
        client.PatchAsJsonAsync($"/api/v1/bookings/{id}", body);

    [Fact]
    public async Task MovingToAFreeTime_Works_AndKeepsThePrice()
    {
        var co = await NewCompany();
        var id = await Counter(co, "18:00");

        var res = await Patch(co.Client, id, new { date = Day(5), startTime = "20:00" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var b = await _fx.LoadBooking(id);
        Assert.Equal((Day(5), "20:00", 60, 20.0), (b!.Date.ToString("yyyy-MM-dd"), b.StartTime, b.Duration, b.TotalAmount));
    }

    [Fact]
    public async Task MovingOntoAnotherBooking_IsRefused_AndNothingChanges()
    {
        var co = await NewCompany();
        await Counter(co, "20:00");
        var id = await Counter(co, "18:00");

        var res = await Patch(co.Client, id, new { startTime = "19:30" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("18:00", (await _fx.LoadBooking(id))!.StartTime);
    }

    [Fact]
    public async Task ABookingDoesNotCollideWithItself()
    {
        var co = await NewCompany();
        var longer = await Counter(co, "18:00");
        var shifted = await Counter(co, "12:00", duration: 120);

        // Half an hour longer, over its own old time.
        var extend = await Patch(co.Client, longer, new { duration = 90 });
        // Half an hour later, still overlapping where it was.
        var shift = await Patch(co.Client, shifted, new { startTime = "12:30" });

        Assert.Equal(HttpStatusCode.OK, extend.StatusCode);
        Assert.Equal(HttpStatusCode.OK, shift.StatusCode);
        var b = await _fx.LoadBooking(longer);
        // A longer game is priced from the list: 20 JOD/h × 1.5h, deposit 20%.
        Assert.Equal((90, 30.0, 6.0), (b!.Duration, b.TotalAmount, b.DepositAmount));
    }

    [Fact]
    public async Task TheNewSlotPassesTheSameChecksAsANewBooking()
    {
        var co = await NewCompany();
        var id = await Counter(co, "18:00");

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(co.Client, id, new { date = Day(-1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(co.Client, id, new { startTime = "06:00" })).StatusCode); // before opening
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(co.Client, id, new { duration = 5 })).StatusCode);
    }

    [Fact]
    public async Task OnlyUpcomingBookingsCanBeChanged()
    {
        var co = await NewCompany();
        var id = await Counter(co, "18:00");
        await co.Client.PatchAsync($"/api/v1/bookings/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(co.Client, id, new { startTime = "20:00" })).StatusCode);
    }

    [Fact]
    public async Task AGivenPrice_NeedsThePermissionToRecordPayments()
    {
        var co = await NewCompany();
        var id = await Counter(co, "18:00");
        var clerk = await _fx.ClerkWith(co.Client, StaffPermissions.BookingsView, StaffPermissions.BookingsManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(clerk, id, new { totalAmount = 5 })).StatusCode);
        Assert.Equal(20, (await _fx.LoadBooking(id))!.TotalAmount, 3);

        // The same clerk can still move it; moving is bookings.manage.
        Assert.Equal(HttpStatusCode.OK, (await Patch(clerk, id, new { startTime = "19:00" })).StatusCode);
        // And sending the price it already has is not a price change.
        Assert.Equal(HttpStatusCode.OK, (await Patch(clerk, id, new { startTime = "19:30", totalAmount = 20 })).StatusCode);
    }

    [Fact]
    public async Task LoweringThePriceBelowWhatWasPaid_ReportsTheDifference_AndLeavesTheMoneyAlone()
    {
        var co = await NewCompany();
        var id = await Counter(co, "18:00", duration: 120, paid: true); // 40 paid

        var res = await Patch(co.Client, id, new { totalAmount = 30 });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!;
        Assert.Contains("10 JOD", body.Message);
        var b = await _fx.LoadBooking(id);
        Assert.Equal((30.0, 40.0, 30.0), (b!.TotalAmount, b.AmountPaid, b.OwnerAmount)); // counter bookings carry no fee
        Assert.Single(await _fx.LoadPayments(id));
    }

    [Fact]
    public async Task PlayersAndOtherCompaniesCannotMoveBookings()
    {
        var co = await NewCompany();
        var id = await Counter(co, "18:00");
        var other = await NewCompany();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Patch(_fx.CreateClientFor(_fx.PlayerId, "player"), id, new { startTime = "20:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Patch(other.Client, id, new { startTime = "20:00" })).StatusCode);
    }

    [Fact]
    public async Task AMovedAppBooking_TellsThePlayer()
    {
        var co = await NewCompany();
        var player = await _fx.CreatePlayer();
        var res = await _fx.CreateClientFor(player.Id, "player").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = co.Venue.Id, sport = "basketball", date = Day(6), startTime = "16:00", duration = 60, paymentMethod = "cliq",
        });
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        Assert.Equal(HttpStatusCode.OK, (await Patch(co.Client, id, new { startTime = "17:00" })).StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == player.Id && n.Type == "booking_moved" && n.ReferenceId == id));
    }

    [Fact]
    public async Task TwoMovesIntoOneFreeSlot_AtTheSameTime_OnlyOneLands()
    {
        var co = await NewCompany();
        var ids = new[] { await Counter(co, "09:00"), await Counter(co, "11:00"), await Counter(co, "13:00") };

        var gate = new TaskCompletionSource();
        var attempts = ids.Select(async id =>
        {
            var client = _fx.CreateClientFor(co.Owner.Id, "venue_owner");
            await gate.Task;
            return (await Patch(client, id, new { startTime = "21:00" })).StatusCode;
        }).ToList();
        gate.SetResult();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, outcomes.Count(s => s == HttpStatusCode.Conflict));
        Assert.Single(await _fx.LoadVenueBookings(co.Venue.Id), b => b.StartTime == "21:00");
    }
}
