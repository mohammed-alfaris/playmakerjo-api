using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.PermanentBookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.PermanentBookings;

/// <summary>
/// Turning one week of a standing reservation into a real booking.
///
/// A standing reservation is a rule, not a booking: it blocks the slot every week and never
/// becomes a row. So there was no way to take the group's money, no way to mark whether they
/// turned up, and nothing reaching the ledger, the reports or the customer's history — and
/// the obvious workaround, booking the slot normally, was REFUSED, because the group's own
/// reservation was sitting in it. Standing groups are the largest slice of a pitch's
/// bookings, so that was the biggest hole left in the product.
///
/// The load-bearing half is <see cref="TheRuleStopsBlockingOnceItsWeekIsRecorded"/>: if the
/// rule kept blocking after materialising, the slot would be consumed twice.
/// </summary>
[Collection("Api")]
public class RecordOccurrenceTests
{
    private readonly DatabaseFixture _fx;

    public RecordOccurrenceTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>The next date at least a week out that falls on the given weekday.</summary>
    private static string NextDate(int dayOfWeek)
    {
        var d = PlatformConstants.JordanToday().AddDays(7);
        while ((int)d.DayOfWeek != dayOfWeek) d = d.AddDays(1);
        return d.ToString("yyyy-MM-dd");
    }

    private async Task<(string VenueId, string PermId, int Dow)> Standing(
        string startTime = "20:00", string? phone = "0791236001")
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        const int dow = 2; // Tuesday
        var res = await owner.PostAsJsonAsync($"/api/v1/venues/{venue.Id}/permanent-bookings", new
        {
            dayOfWeek = dow, startTime, duration = 60,
            label = "Mohammed weekly", customerPhone = phone, customerName = "محمد الفارس",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<PermanentBookingDto>>())!.Data!;
        return (venue.Id, dto.Id, dow);
    }

    private async Task<HttpResponseMessage> Record(string permId, string date) =>
        await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .PostAsJsonAsync($"/api/v1/permanent-bookings/{permId}/record", new { date });

    [Fact]
    public async Task RecordingAWeekCreatesACollectableBooking()
    {
        var (_, permId, dow) = await Standing();

        var res = await Record(permId, NextDate(dow));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rec = (await res.Content.ReadFromJsonAsync<ApiResponse<RecordedOccurrenceDto>>())!.Data!;

        Assert.Equal("confirmed", rec.Status);
        Assert.Equal("20:00", rec.StartTime);
        Assert.Equal("محمد الفارس", rec.CustomerName);

        // Unpaid on purpose: a weekly group pays cash on the night. Marking it paid here
        // would put money in an append-only ledger before anyone handed anything over.
        Assert.Equal(0, rec.AmountPaid);
        Assert.True(rec.TotalAmount > 0);

        var booking = await _fx.LoadBooking(rec.BookingId);
        Assert.Equal(permId, booking!.PermanentBookingId);
        Assert.NotNull(booking.CustomerId);
        Assert.Equal(0, booking.SystemFee);
    }

    [Fact]
    public async Task TheRuleStopsBlockingOnceItsWeekIsRecorded()
    {
        // Without this the pitch is consumed twice — invisible on a single-capacity venue,
        // but on a subdividable one the owner silently loses a game he could have sold.
        var (venueId, permId, dow) = await Standing("20:00");
        var date = NextDate(dow);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var slots = await owner.GetFromJsonAsync<System.Text.Json.JsonDocument>(
            $"/api/v1/venues/{venueId}/available-slots?date={date}");
        var before = slots!.RootElement.GetProperty("data").GetProperty("bookedSlots").GetArrayLength();

        Assert.Equal(HttpStatusCode.OK, (await Record(permId, date)).StatusCode);

        slots = await owner.GetFromJsonAsync<System.Text.Json.JsonDocument>(
            $"/api/v1/venues/{venueId}/available-slots?date={date}");
        var after = slots!.RootElement.GetProperty("data").GetProperty("bookedSlots").GetArrayLength();

        // The rule dropped out and the booking took its place — one occupant, not two.
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task RecordingTwiceReturnsTheSameBooking()
    {
        // Two clerks reaching for the same group's money is a normal Tuesday.
        var (_, permId, dow) = await Standing();
        var date = NextDate(dow);

        var a = (await (await Record(permId, date)).Content.ReadFromJsonAsync<ApiResponse<RecordedOccurrenceDto>>())!;
        var b = (await (await Record(permId, date)).Content.ReadFromJsonAsync<ApiResponse<RecordedOccurrenceDto>>())!;

        Assert.Equal(a.Data!.BookingId, b.Data!.BookingId);
        Assert.Equal("Already recorded", b.Message);
    }

    [Fact]
    public async Task TheMoneyCanActuallyBeCollected()
    {
        // The whole point. Before this the group's cash had nowhere to go.
        var (_, permId, dow) = await Standing();
        var rec = (await (await Record(permId, NextDate(dow)))
            .Content.ReadFromJsonAsync<ApiResponse<RecordedOccurrenceDto>>())!.Data!;

        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var paid = await owner.PatchAsync($"/api/v1/bookings/{rec.BookingId}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var booking = await _fx.LoadBooking(rec.BookingId);
        Assert.Equal(booking!.TotalAmount, booking.AmountPaid);

        // And it reached the ledger, with a name against it.
        var row = Assert.Single(await _fx.LoadPayments(rec.BookingId));
        Assert.Equal(_fx.OwnerAId, row.RecordedByUserId);
    }

    [Fact]
    public async Task ADateOnTheWrongWeekdayIsRefused()
    {
        var (_, permId, dow) = await Standing();
        var wrongDay = DateTime.Parse(NextDate(dow)).AddDays(1).ToString("yyyy-MM-dd");

        var res = await Record(permId, wrongDay);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ACancelledStandingBookingCannotBeRecorded()
    {
        var (_, permId, dow) = await Standing();
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        await owner.PatchAsync($"/api/v1/permanent-bookings/{permId}/cancel", null);

        Assert.Equal(HttpStatusCode.BadRequest, (await Record(permId, NextDate(dow))).StatusCode);
    }

    [Fact]
    public async Task AReadOnlyClerkCannotRecordAWeek()
    {
        var (_, permId, dow) = await Standing();
        var clerk = await _fx.CreateClientForUserAsync(_fx.StaffAReadId);

        var res = await clerk.PostAsJsonAsync(
            $"/api/v1/permanent-bookings/{permId}/record", new { date = NextDate(dow) });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AWriteClerkCan()
    {
        // Taking the weekly group's money is exactly the counter clerk's job.
        var (_, permId, dow) = await Standing();
        var clerk = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        var res = await clerk.PostAsJsonAsync(
            $"/api/v1/permanent-bookings/{permId}/record", new { date = NextDate(dow) });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
