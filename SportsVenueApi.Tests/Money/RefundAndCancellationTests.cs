using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Reports;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Money;

/// <summary>
/// Money going back: refunds and corrections as negative ledger rows, and the venue's
/// free-cancellation window deciding what a cancellation does to money already paid.
/// </summary>
[Collection("Api")]
public class RefundAndCancellationTests
{
    private readonly DatabaseFixture _fx;

    public RefundAndCancellationTests(DatabaseFixture fx) => _fx = fx;

    private static string Day(int ahead) => PlatformConstants.JordanToday().AddDays(ahead).ToString("yyyy-MM-dd");

    private sealed record Co(User Owner, HttpClient Client, Venue Venue);

    private async Task<Co> NewCompany(int freeCancelHours = 24)
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id, v => v.FreeCancelHours = freeCancelHours);
        return new Co(owner, _fx.CreateClientFor(owner.Id, "venue_owner"), venue);
    }

    /// <summary>A counter booking, paid in full (20 JOD/h × 2h = 40) at the desk.</summary>
    private static async Task<string> PaidCounterBooking(Co co, int daysAhead, string start = "18:00")
    {
        var res = await co.Client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = co.Venue.Id, sport = "basketball", date = Day(daysAhead), startTime = start, duration = 120,
            paymentMethod = "cliq", isManual = true, customerPaid = true,
            customerPhone = "0791234" + Random.Shared.Next(100, 999), customerName = "Refund Test",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
    }

    private async Task AssertLedgerAddsUp(string bookingId)
    {
        var booking = await _fx.LoadBooking(bookingId);
        var rows = await _fx.LoadPayments(bookingId);
        Assert.Equal(booking!.AmountPaid, rows.Sum(p => p.Amount), 3);
    }

    private static Task<HttpResponseMessage> Cancel(HttpClient client, string id, string? refund = null) =>
        refund == null
            ? client.PatchAsync($"/api/v1/bookings/{id}/cancel", null)
            : client.PatchAsJsonAsync($"/api/v1/bookings/{id}/cancel", new { refund });

    // ── Refunds ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARefund_IsANegativeRow_AndTheLedgerStillAddsUp()
    {
        var co = await NewCompany();
        var id = await PaidCounterBooking(co, 3);

        var res = await co.Client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund",
            new { amount = 15, kind = "refund", note = "Lights failed for 30 minutes" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var booking = await _fx.LoadBooking(id);
        Assert.Equal(25, booking!.AmountPaid, 3);
        var last = (await _fx.LoadPayments(id)).OrderBy(p => p.Date).Last();
        Assert.Equal((-15.0, "refund", co.Owner.Id), (last.Amount, last.Kind, last.RecordedByUserId));
        await AssertLedgerAddsUp(id);

        // Every total sums the rows, so "collected" is already net of the refund.
        var today = PlatformConstants.JordanToday().ToString("yyyy-MM-dd");
        var money = (await (await co.Client.GetAsync($"/api/v1/reports/money?from={today}&to={today}"))
            .Content.ReadFromJsonAsync<ApiResponse<MoneyReport>>())!.Data!;
        Assert.Equal(25, money.Collected.Value);
    }

    [Fact]
    public async Task RefundingNothing_OrMoreThanWasPaid_IsRefused()
    {
        var co = await NewCompany();
        var id = await PaidCounterBooking(co, 3);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await co.Client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 41, kind = "refund" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await co.Client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 0, kind = "refund" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await co.Client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 5, kind = "gift" })).StatusCode);
        Assert.Equal(40, (await _fx.LoadBooking(id))!.AmountPaid, 3);
    }

    [Fact]
    public async Task ACorrection_IsRecordedAsSuch()
    {
        var co = await NewCompany();
        var id = await PaidCounterBooking(co, 3);

        await co.Client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 40, kind = "correction", note = "Entered twice" });

        var booking = await _fx.LoadBooking(id);
        Assert.Equal(0, booking!.AmountPaid, 3);
        Assert.False(booking.DepositPaid);
        Assert.Contains(await _fx.LoadPayments(id), p => p.Kind == "correction" && p.Amount == -40);
    }

    // ── Who may move money back ──────────────────────────────────────────────

    [Fact]
    public async Task AClerkWhoCannotRecordPayments_CanCancel_ButTheMoneyStays()
    {
        var co = await NewCompany();
        var clerk = await _fx.ClerkWith(co.Client, StaffPermissions.BookingsView, StaffPermissions.BookingsManage);

        var refundable = await PaidCounterBooking(co, 5, "10:00");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await clerk.PostAsJsonAsync($"/api/v1/bookings/{refundable}/refund", new { amount = 5, kind = "refund" })).StatusCode);

        // The rule would refund (5 days ahead), but this clerk cannot move money: the booking
        // is cancelled and the 40 stays recorded for the owner to decide.
        Assert.Equal(HttpStatusCode.OK, (await Cancel(clerk, refundable)).StatusCode);
        Assert.Equal(40, (await _fx.LoadBooking(refundable))!.AmountPaid, 3);

        // Asking outright for a refund is refused rather than silently ignored.
        var other = await PaidCounterBooking(co, 5, "13:00");
        Assert.Equal(HttpStatusCode.Forbidden, (await Cancel(clerk, other, "all")).StatusCode);
        Assert.Equal("confirmed", (await _fx.LoadBooking(other))!.Status);
    }

    // ── The free-cancellation window ─────────────────────────────────────────

    [Fact]
    public async Task CancellingBeforeTheWindow_RefundsEverything()
    {
        var co = await NewCompany(freeCancelHours: 24);
        var id = await PaidCounterBooking(co, 5);

        var res = await Cancel(co.Client, id);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var booking = await _fx.LoadBooking(id);
        Assert.Equal(("cancelled", 0.0), (booking!.Status, booking.AmountPaid));
        // The rule travels with the booking, so a cancel screen can preview it.
        var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!;
        Assert.Equal(24, dto.Venue.FreeCancelHours);
        await AssertLedgerAddsUp(id);
    }

    [Fact]
    public async Task CancellingInsideTheWindow_KeepsTheMoney_UnlessTheVenueSaysRefundAll()
    {
        var co = await NewCompany(freeCancelHours: 24 * 30); // 5 days ahead is inside a 30-day window
        var kept = await PaidCounterBooking(co, 5, "10:00");
        var overridden = await PaidCounterBooking(co, 5, "13:00");

        await Cancel(co.Client, kept);
        await Cancel(co.Client, overridden, "all");

        Assert.Equal(40, (await _fx.LoadBooking(kept))!.AmountPaid, 3);
        Assert.Equal(0, (await _fx.LoadBooking(overridden))!.AmountPaid, 3);
        await AssertLedgerAddsUp(overridden);
    }

    [Fact]
    public async Task KeepIsAlwaysAllowed_EvenWhenTheRuleWouldRefund()
    {
        var co = await NewCompany(freeCancelHours: 0); // always free
        var id = await PaidCounterBooking(co, 5);

        await Cancel(co.Client, id, "none");

        Assert.Equal(40, (await _fx.LoadBooking(id))!.AmountPaid, 3);
    }

    [Fact]
    public async Task APlayerCancellingEarly_GetsTheRule_RecordedAsTheRulesDecision()
    {
        var co = await NewCompany();
        var player = await _fx.CreatePlayer();
        var playerClient = _fx.CreateClientFor(player.Id, "player");
        var res = await playerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = co.Venue.Id, sport = "basketball", date = Day(6), startTime = "16:00", duration = 60, paymentMethod = "cliq",
        });
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
        Assert.Equal(HttpStatusCode.OK, (await co.Client.PatchAsync($"/api/v1/bookings/{id}/mark-paid", null)).StatusCode);

        // A player cannot choose "all": whatever they send, the venue's rule applies.
        Assert.Equal(HttpStatusCode.OK, (await Cancel(playerClient, id, "none")).StatusCode);

        var refund = (await _fx.LoadPayments(id)).Single(p => p.Amount < 0);
        Assert.Equal(-20, refund.Amount, 3);
        Assert.Null(refund.RecordedByUserId);
        await AssertLedgerAddsUp(id);
    }

    [Fact]
    public async Task CancellingASeries_AppliesTheRuleToEachWeekOnItsOwn()
    {
        // A 10-day window: next week's session (7 days out) is inside it, the one after (14) is not.
        var co = await NewCompany(freeCancelHours: 24 * 10);
        var player = await _fx.CreatePlayer();
        var res = await _fx.CreateClientFor(player.Id, "player").PostAsJsonAsync("/api/v1/bookings/recurring", new
        {
            venueId = co.Venue.Id, sport = "basketball", startDate = Day(7), endDate = Day(14), startTime = "10:00",
            duration = 60, recurrenceType = "weekly", paymentMethod = "cliq", conflictPolicy = "skip",
        });
        var created = (await res.Content.ReadFromJsonAsync<ApiResponse<RecurringBookingResponse>>())!.Data!;
        Assert.Equal(2, created.Created.Count);
        foreach (var b in created.Created)
            await co.Client.PatchAsync($"/api/v1/bookings/{b.Id}/mark-paid", null);

        var cancel = await co.Client.PatchAsync($"/api/v1/bookings/recurring/{created.GroupId}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var near = await _fx.LoadBooking(created.Created[0].Id);
        var far = await _fx.LoadBooking(created.Created[1].Id);
        Assert.Equal(("cancelled", 20.0), (near!.Status, near.AmountPaid)); // inside the window: kept
        Assert.Equal(("cancelled", 0.0), (far!.Status, far.AmountPaid));    // outside: refunded
        await AssertLedgerAddsUp(far.Id);
    }
}
