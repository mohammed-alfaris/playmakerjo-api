using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

/// <summary>
/// Availability was a read-then-write with nothing in between: two people booking the same
/// 21:00 slot both read "free", both passed the capacity check, and both committed. On a
/// CliQ platform each then transfers a real deposit to the owner's alias for a pitch only
/// one of them can have, and the owner turns someone away on the night.
///
/// A unique index cannot express the rule — a subdividable 11-aside pitch legitimately holds
/// four overlapping 5-aside games, so what is enforced is a SUM against a capacity budget,
/// not uniqueness. The fix serialises per venue with a row lock, and these tests are the
/// only thing that proves it: single-threaded tests pass either way.
/// </summary>
[Collection("Api")]
public class ConcurrentBookingTests
{
    private readonly DatabaseFixture _fx;

    public ConcurrentBookingTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(21).ToString("yyyy-MM-dd");

    private static object Body(string venueId, string startTime, string? pitchSize = null) => new
    {
        venueId,
        sport = pitchSize == null ? "basketball" : "football",
        pitchSize,
        date = FutureDate,
        startTime,
        duration = 60,
        paymentMethod = "cliq",
    };

    /// <summary>Fires N requests at once and reports how each landed.</summary>
    private async Task<List<HttpStatusCode>> RaceAsync(int count, Func<HttpClient> clientFactory, object body)
    {
        // Separate clients so nothing is serialised on the caller's side — the point is to
        // hit the server genuinely concurrently.
        var clients = Enumerable.Range(0, count).Select(_ => clientFactory()).ToList();

        var gate = new TaskCompletionSource();
        var attempts = clients.Select(async client =>
        {
            await gate.Task;
            var res = await client.PostAsJsonAsync("/api/v1/bookings", body);
            return res.StatusCode;
        }).ToList();

        gate.SetResult();
        var results = await Task.WhenAll(attempts);
        return results.ToList();
    }

    [Fact]
    public async Task FiveSimultaneousBookings_ForOneSlot_YieldExactlyOneWinner()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);

        var outcomes = await RaceAsync(
            5,
            () => _fx.CreateClientFor(_fx.PlayerId, "player"),
            Body(venue.Id, "20:00"));

        var created = outcomes.Count(s => s == HttpStatusCode.OK);
        var rejected = outcomes.Count(s => s == HttpStatusCode.Conflict);

        Assert.Equal(1, created);
        Assert.Equal(4, rejected);

        // And the database agrees — the count is the thing that actually matters.
        var rows = await _fx.LoadVenueBookings(venue.Id);
        Assert.Single(rows, b => b.StartTime == "20:00" && b.Status != "cancelled");
    }

    [Fact]
    public async Task ConcurrentBookings_ForDifferentSlots_AllSucceed()
    {
        // The lock must not turn the venue into a queue that rejects legitimate bookings.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var times = new[] { "09:00", "10:00", "11:00", "12:00", "13:00" };

        var attempts = times.Select(async time =>
        {
            var client = _fx.CreateClientFor(_fx.PlayerId, "player");
            var res = await client.PostAsJsonAsync("/api/v1/bookings", Body(venue.Id, time));
            return res.StatusCode;
        }).ToList();

        var outcomes = await Task.WhenAll(attempts);

        Assert.All(outcomes, s => Assert.Equal(HttpStatusCode.OK, s));
        var rows = await _fx.LoadVenueBookings(venue.Id);
        Assert.Equal(5, rows.Count(b => b.Status != "cancelled"));
    }

    [Fact]
    public async Task ConcurrentBookings_OnDifferentVenues_DoNotBlockEachOther()
    {
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);

        var attempts = new[] { venueA.Id, venueB.Id }.Select(async venueId =>
        {
            var client = _fx.CreateClientFor(_fx.PlayerId, "player");
            var res = await client.PostAsJsonAsync("/api/v1/bookings", Body(venueId, "14:00"));
            return res.StatusCode;
        }).ToList();

        var outcomes = await Task.WhenAll(attempts);
        Assert.All(outcomes, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    [Fact]
    public async Task SubdividablePitch_FillsToCapacityUnderLoad_AndNoFurther()
    {
        // The subtle case, and the reason a unique index would have been wrong. An 11-aside
        // pitch is 4 capacity units; a 6-aside game costs 1. Five simultaneous attempts must
        // leave exactly four bookings, not one and not five.
        var (venue, _) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);

        var outcomes = await RaceAsync(
            5,
            () => _fx.CreateClientFor(_fx.PlayerId, "player"),
            Body(venue.Id, "16:00", pitchSize: "6"));

        Assert.Equal(4, outcomes.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(1, outcomes.Count(s => s == HttpStatusCode.Conflict));

        var rows = await _fx.LoadVenueBookings(venue.Id);
        Assert.Equal(4, rows.Count(b => b.StartTime == "16:00" && b.Status != "cancelled"));
    }

    [Fact]
    public async Task ARejectedAttempt_LeavesNothingBehind()
    {
        // The losing requests roll back, so a failed race must not leave a half-written
        // booking or a stray customer row.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);

        await RaceAsync(3, () => _fx.CreateClientFor(_fx.PlayerId, "player"), Body(venue.Id, "18:30"));

        var rows = await _fx.LoadVenueBookings(venue.Id);
        Assert.Single(rows, b => b.StartTime == "18:30" && b.Status != "cancelled");
    }
}
