using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Customers;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Customers;

/// <summary>
/// The numbers an owner actually reads. The one that carries the whole design is
/// <c>attended</c>: "completed" is only ever written by a manual tap and an owner has no
/// reason to tap when nothing went wrong, so counting only "completed" would show zero
/// attendance for every customer forever. Presence is assumed, absence is declared.
/// </summary>
[Collection("Api")]
public class CustomerStatsTests
{
    private readonly DatabaseFixture _fx;

    public CustomerStatsTests(DatabaseFixture fx) => _fx = fx;

    private static DateTime Today => PlatformConstants.JordanToday();

    private async Task<Customer> NewCustomer(string ownerId, string? name = null)
    {
        return await _fx.Insert(new Customer
        {
            OwnerId = ownerId,
            Phone = "+96279" + Random.Shared.Next(1000000, 9999999),
            Name = name ?? "Stats Subject",
        });
    }

    private async Task<Booking> Seed(
        string venueId, string customerId, int daysAgo, string status,
        double total = 20, double paid = 20, bool isManual = true)
    {
        return await _fx.Insert(new Booking
        {
            VenueId = venueId,
            PlayerId = _fx.PlayerId,
            CustomerId = customerId,
            Sport = "basketball",
            Date = Today.AddDays(-daysAgo),
            StartTime = "10:00",
            Duration = 60,
            Amount = total,
            TotalAmount = total,
            AmountPaid = paid,
            Status = status,
            IsManual = isManual,
        });
    }

    private async Task<CustomerStats> StatsFor(string customerId, string ownerId = "")
    {
        var client = _fx.CreateClientFor(
            string.IsNullOrEmpty(ownerId) ? _fx.OwnerAId : ownerId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/{customerId}");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerDetailResponse>>();
        return body!.Data!.Stats;
    }

    // ------------------------------------------------------------------ attendance

    [Fact]
    public async Task PastConfirmedBooking_CountsAsAttended()
    {
        // The crucial case. Nobody tapped anything; the slot simply happened.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 3, "confirmed");

        var stats = await StatsFor(customer.Id);

        Assert.Equal(1, stats.Attended);
        Assert.Equal(0, stats.NoShow);
    }

    [Fact]
    public async Task ExplicitlyCompletedBooking_CountsAsAttended()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 4, "completed");

        Assert.Equal(1, (await StatsFor(customer.Id)).Attended);
    }

    [Fact]
    public async Task NoShow_IsNotCountedAsAttended()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 5, "no_show");

        var stats = await StatsFor(customer.Id);
        Assert.Equal(0, stats.Attended);
        Assert.Equal(1, stats.NoShow);
    }

    [Fact]
    public async Task FutureConfirmedBooking_IsUpcomingNotAttended()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: -5, "confirmed");

        var stats = await StatsFor(customer.Id);
        Assert.Equal(0, stats.Attended);
        Assert.Equal(1, stats.Upcoming);
    }

    [Fact]
    public async Task CancelledBookings_AreExcludedFromTheTotal()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 6, "confirmed");
        await Seed(venue.Id, customer.Id, daysAgo: 7, "cancelled");

        var stats = await StatsFor(customer.Id);
        Assert.Equal(1, stats.TotalBookings);
        Assert.Equal(1, stats.Cancelled);
    }

    // ------------------------------------------------------------------- the money

    [Fact]
    public async Task UnpaidBookings_ShowAsOwing()
    {
        // The phone booking that was never paid — the expensive kind.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 2, "confirmed", total: 30, paid: 0);
        await Seed(venue.Id, customer.Id, daysAgo: 3, "completed", total: 20, paid: 20);

        var stats = await StatsFor(customer.Id);
        Assert.Equal(1, stats.Unpaid);
        Assert.Equal(30, stats.AmountOwed, 3);
    }

    [Fact]
    public async Task CancelledBooking_IsNotCountedAsOwing()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 2, "cancelled", total: 30, paid: 0);

        var stats = await StatsFor(customer.Id);
        Assert.Equal(0, stats.Unpaid);
        Assert.Equal(0, stats.AmountOwed, 3);
    }

    // --------------------------------------------------------------------- channel

    [Fact]
    public async Task CounterAndAppBookings_AreCountedSeparately()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 8, "completed", isManual: true);
        await Seed(venue.Id, customer.Id, daysAgo: 9, "completed", isManual: false);
        await Seed(venue.Id, customer.Id, daysAgo: 10, "completed", isManual: false);

        var stats = await StatsFor(customer.Id);
        Assert.Equal(1, stats.ViaCounter);
        Assert.Equal(2, stats.ViaApp);
    }

    // ------------------------------------------------------------------- timeline

    [Fact]
    public async Task LastVisitAndCustomerSince_UseTheRightEnds()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 40, "completed");
        await Seed(venue.Id, customer.Id, daysAgo: 5, "completed");
        await Seed(venue.Id, customer.Id, daysAgo: -3, "confirmed");   // future

        var stats = await StatsFor(customer.Id);
        Assert.Equal(Today.AddDays(-5).ToString("yyyy-MM-dd"), stats.LastVisit);
        Assert.Equal(Today.AddDays(-40).ToString("yyyy-MM-dd"), stats.CustomerSince);
        Assert.Equal(5, stats.DaysSinceLastVisit);
    }

    // ---------------------------------------------------------------- segments

    [Fact]
    public async Task FourVisitsInNinetyDays_MakesThemRegular()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        foreach (var d in new[] { 5, 12, 19, 26 })
            await Seed(venue.Id, customer.Id, daysAgo: d, "completed");

        var stats = await StatsFor(customer.Id);
        Assert.True(stats.IsRegular);
        Assert.False(stats.IsLapsed);
    }

    [Fact]
    public async Task TwoVisitsAndThirtyDaysSilence_MakesThemLapsed()
    {
        // The one that makes money: a regular quietly stopped coming and nothing else
        // would ever have told the owner.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 60, "completed");
        await Seed(venue.Id, customer.Id, daysAgo: 45, "completed");

        var stats = await StatsFor(customer.Id);
        Assert.True(stats.IsLapsed);
        Assert.False(stats.IsRegular);
    }

    [Fact]
    public async Task ASingleVisit_IsNewNotLapsed()
    {
        // Someone who came once and never returned is not a lost regular — chasing them
        // as if they were would waste the owner's time.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 90, "completed");

        var stats = await StatsFor(customer.Id);
        Assert.True(stats.IsNew);
        Assert.False(stats.IsLapsed);
    }

    [Fact]
    public async Task TwoNoShows_MakeThemUnreliable()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 10, "no_show");
        await Seed(venue.Id, customer.Id, daysAgo: 20, "no_show");

        Assert.True((await StatsFor(customer.Id)).IsUnreliable);
    }

    [Fact]
    public async Task NoShowRate_IsProportionalNotAbsolute()
    {
        // 1 miss in 20 is a different person from 1 miss in 2. The rate is what the red
        // chip should key off, not the raw count.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var loyal = await NewCustomer(_fx.OwnerAId, "Loyal");
        for (var i = 1; i <= 19; i++)
            await Seed(venue.Id, loyal.Id, daysAgo: i, "completed");
        await Seed(venue.Id, loyal.Id, daysAgo: 25, "no_show");

        var stats = await StatsFor(loyal.Id);
        Assert.Equal(5, stats.NoShowRate, 1);
        Assert.False(stats.IsUnreliable);
    }

    // ---------------------------------------------------------------- lookup

    [Fact]
    public async Task Lookup_UnknownNumber_Returns200WithNull()
    {
        // Not a 404: "I don't know them" is the normal answer when a new customer walks in,
        // and an error response would put a red state in front of the owner every time.
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/customers/lookup?phone=0799999999");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerLookupResponse?>>();
        Assert.Null(body!.Data);
    }

    [Fact]
    public async Task Lookup_FindsTheCustomerWhicheverWayTheNumberIsTyped()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await _fx.Insert(new Customer
        {
            OwnerId = _fx.OwnerAId,
            Phone = "+962791234599",
            Name = "Lookup Target",
        });
        await Seed(venue.Id, customer.Id, daysAgo: 2, "completed");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        foreach (var spelling in new[] { "0791234599", "+962791234599", "٠٧٩١٢٣٤٥٩٩" })
        {
            var res = await client.GetAsync($"/api/v1/customers/lookup?phone={Uri.EscapeDataString(spelling)}");
            var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerLookupResponse?>>();
            Assert.Equal(customer.Id, body!.Data?.Id);
            Assert.Equal(1, body.Data!.Stats.Attended);
        }
    }

    [Fact]
    public async Task Lookup_NeverReachesAcrossOwners()
    {
        var theirs = await _fx.Insert(new Customer
        {
            OwnerId = _fx.OwnerBId,
            Phone = "+962791234588",
            Name = "Competitor's customer",
        });

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/customers/lookup?phone=0791234588");
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerLookupResponse?>>();

        Assert.Null(body!.Data);
        Assert.NotNull(theirs);
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    public async Task ACompetitorsCustomer_CannotBeRead()
    {
        var theirs = await NewCustomer(_fx.OwnerBId, "Theirs");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/{theirs.Id}");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Player_IsRefusedTheCustomerBook()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/customers/lookup?phone=0791111111")).StatusCode);
    }

    [Fact]
    public async Task Staff_SeeTheirEmployersBook()
    {
        var customer = await NewCustomer(_fx.OwnerAId, "Employer's customer");

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var res = await client.GetAsync($"/api/v1/customers/{customer.Id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task UnlinkedStaff_GetNothing()
    {
        var client = _fx.CreateClientFor(_fx.StaffUnlinkedId, "venue_staff");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/customers")).StatusCode);
    }

    // -------------------------------------------------------------- edit & archive

    [Fact]
    public async Task NameAndNote_CanBeCorrected()
    {
        var customer = await NewCustomer(_fx.OwnerAId, "Typo Name");
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await client.PatchAsJsonAsync($"/api/v1/customers/{customer.Id}",
            new { name = "Corrected Name", note = "always 15 minutes late" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerResponse>>();
        Assert.Equal("Corrected Name", body!.Data!.Name);
        Assert.Equal("always 15 minutes late", body.Data.Note);
    }

    [Fact]
    public async Task Archive_HidesThemFromTheList_WithoutTouchingTheirBookings()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId, "Junk Row");
        var booking = await Seed(venue.Id, customer.Id, daysAgo: 3, "completed");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        await client.PatchAsync($"/api/v1/customers/{customer.Id}/archive", null);

        var list = await client.GetAsync("/api/v1/customers?limit=100");
        Assert.DoesNotContain(customer.Id, await list.Content.ReadAsStringAsync());

        // The booking is financial history and must survive.
        Assert.NotNull(await _fx.LoadBooking(booking.Id));
    }

    [Fact]
    public async Task LapsedSegment_ReturnsOnlyTheOnesWhoStoppedComing()
    {
        // Deliberately NOT OwnerC — the fixture documents that one as owning no venues and
        // UserCreationTests relies on it, so creating a venue for them here breaks an
        // unrelated test. Assertions below are relative, so sharing OwnerB is harmless.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var gone = await NewCustomer(_fx.OwnerBId, "Gone Quiet");
        await Seed(venue.Id, gone.Id, daysAgo: 70, "completed");
        await Seed(venue.Id, gone.Id, daysAgo: 50, "completed");

        var active = await NewCustomer(_fx.OwnerBId, "Still Here");
        await Seed(venue.Id, active.Id, daysAgo: 3, "completed");
        await Seed(venue.Id, active.Id, daysAgo: 10, "completed");

        var client = _fx.CreateClientFor(_fx.OwnerBId, "venue_owner");
        var res = await client.GetAsync("/api/v1/customers?segment=lapsed&limit=100");
        var payload = await res.Content.ReadAsStringAsync();

        Assert.Contains(gone.Id, payload);
        Assert.DoesNotContain(active.Id, payload);
    }
}
