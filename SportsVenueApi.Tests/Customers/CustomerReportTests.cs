using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Customers;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Customers;

/// <summary>
/// The month in one screen. The number that carries it is the new-vs-returning split: new
/// customers cost money to find, returning ones are the business, and a month full of new
/// faces with nobody coming back is a leaking bucket that nothing else in the product would
/// reveal.
/// </summary>
[Collection("Api")]
public class CustomerReportTests
{
    private readonly DatabaseFixture _fx;

    public CustomerReportTests(DatabaseFixture fx) => _fx = fx;

    private static DateTime Today => PlatformConstants.JordanToday();
    private static DateTime MonthStart => new(Today.Year, Today.Month, 1);

    /// <summary>Owner D exists only for this file, so counts are not disturbed by other tests.</summary>
    private async Task<string> IsolatedOwner()
    {
        var owner = await _fx.Insert(new User
        {
            Name = "Report Owner",
            Email = $"report-owner-{Guid.NewGuid():N}@test.local",
            PasswordHash = "never-logs-in",
            Role = "venue_owner",
            Status = "active",
        });
        return owner.Id;
    }

    private async Task<Customer> NewCustomer(string ownerId, string name)
    {
        return await _fx.Insert(new Customer
        {
            OwnerId = ownerId,
            Phone = "+96279" + Random.Shared.Next(1000000, 9999999),
            Name = name,
        });
    }

    private async Task Seed(string venueId, string customerId, DateTime date, string status = "completed")
    {
        await _fx.Insert(new Booking
        {
            VenueId = venueId,
            PlayerId = _fx.PlayerId,
            CustomerId = customerId,
            Sport = "basketball",
            Date = date,
            StartTime = "10:00",
            Duration = 60,
            Amount = 20,
            TotalAmount = 20,
            AmountPaid = 20,
            Status = status,
            IsManual = true,
        });
    }

    private async Task<CustomerReportResponse> Report(string ownerId)
    {
        var client = _fx.CreateClientFor(ownerId, "venue_owner");
        var res = await client.GetAsync("/api/v1/customers/report");
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerReportResponse>>();
        return body!.Data!;
    }

    [Fact]
    public async Task Route_IsNotShadowedByCustomerIdRoute()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/customers/report");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task SplitsNewFromReturning()
    {
        var ownerId = await IsolatedOwner();
        var venue = await _fx.CreateBasketballVenue(ownerId);

        // Came for the first time this month.
        var fresh = await NewCustomer(ownerId, "First Timer");
        await Seed(venue.Id, fresh.Id, MonthStart.AddDays(2));

        // Was already a customer before this month, and came back.
        var loyal = await NewCustomer(ownerId, "Came Back");
        await Seed(venue.Id, loyal.Id, MonthStart.AddMonths(-2));
        await Seed(venue.Id, loyal.Id, MonthStart.AddDays(3));

        var report = await Report(ownerId);

        Assert.Equal(2, report.Active);
        Assert.Equal(1, report.NewCustomers);
        Assert.Equal(1, report.Returning);
        Assert.Equal(50, report.ReturnRate, 1);
    }

    [Fact]
    public async Task CustomersWhoDidNotComeThisMonth_AreNotActive()
    {
        var ownerId = await IsolatedOwner();
        var venue = await _fx.CreateBasketballVenue(ownerId);

        var absent = await NewCustomer(ownerId, "Not This Month");
        await Seed(venue.Id, absent.Id, MonthStart.AddMonths(-3));

        var report = await Report(ownerId);

        Assert.Equal(0, report.Active);
        Assert.Equal(1, report.TotalCustomers);
    }

    [Fact]
    public async Task TopCustomers_AreRankedByVisitsThisMonth()
    {
        var ownerId = await IsolatedOwner();
        var venue = await _fx.CreateBasketballVenue(ownerId);

        var heavy = await NewCustomer(ownerId, "Three Visits");
        foreach (var d in new[] { 1, 5, 9 }) await Seed(venue.Id, heavy.Id, MonthStart.AddDays(d));

        var light = await NewCustomer(ownerId, "One Visit");
        await Seed(venue.Id, light.Id, MonthStart.AddDays(2));

        var report = await Report(ownerId);

        Assert.Equal(heavy.Id, report.TopCustomers[0].Id);
        Assert.Equal(3, report.TopCustomers[0].Visits);
        Assert.Equal(light.Id, report.TopCustomers[1].Id);
    }

    [Fact]
    public async Task LapsedList_IsTheWinBackList()
    {
        var ownerId = await IsolatedOwner();
        var venue = await _fx.CreateBasketballVenue(ownerId);

        // Came twice, then went quiet — a real loss worth chasing.
        var lost = await NewCustomer(ownerId, "Gone Quiet");
        await Seed(venue.Id, lost.Id, Today.AddDays(-70));
        await Seed(venue.Id, lost.Id, Today.AddDays(-45));

        // Came once, long ago. NOT a lost regular — chasing them wastes the owner's time.
        var oneOff = await NewCustomer(ownerId, "One And Done");
        await Seed(venue.Id, oneOff.Id, Today.AddDays(-90));

        // Still around.
        var active = await NewCustomer(ownerId, "Still Here");
        await Seed(venue.Id, active.Id, Today.AddDays(-3));
        await Seed(venue.Id, active.Id, Today.AddDays(-10));

        var report = await Report(ownerId);

        Assert.Contains(report.Lapsed, x => x.Id == lost.Id);
        Assert.DoesNotContain(report.Lapsed, x => x.Id == oneOff.Id);
        Assert.DoesNotContain(report.Lapsed, x => x.Id == active.Id);
        Assert.Equal(1, report.LapsedCount);
    }

    [Fact]
    public async Task TrendCoversSixMonths()
    {
        var ownerId = await IsolatedOwner();
        var report = await Report(ownerId);

        Assert.Equal(6, report.Trend.Count);
        Assert.Equal(MonthStart.ToString("yyyy-MM"), report.Trend[^1].Month);
        Assert.Equal(MonthStart.AddMonths(-5).ToString("yyyy-MM"), report.Trend[0].Month);
    }

    [Fact]
    public async Task NoShowsAreCountedButNotAsVisits()
    {
        var ownerId = await IsolatedOwner();
        var venue = await _fx.CreateBasketballVenue(ownerId);

        var c = await NewCustomer(ownerId, "Missed One");
        await Seed(venue.Id, c.Id, MonthStart.AddDays(1), "completed");
        await Seed(venue.Id, c.Id, MonthStart.AddDays(4), "no_show");

        var report = await Report(ownerId);

        Assert.Equal(1, report.Visits);
        Assert.Equal(1, report.NoShows);
    }

    [Fact]
    public async Task AnExplicitMonthCanBeRequested()
    {
        var ownerId = await IsolatedOwner();
        var venue = await _fx.CreateBasketballVenue(ownerId);
        var lastMonth = MonthStart.AddMonths(-1);

        var c = await NewCustomer(ownerId, "Last Month Only");
        await Seed(venue.Id, c.Id, lastMonth.AddDays(4));

        var client = _fx.CreateClientFor(ownerId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/report?month={lastMonth:yyyy-MM}");
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<CustomerReportResponse>>();

        Assert.Equal(lastMonth.ToString("yyyy-MM"), body!.Data!.Month);
        Assert.Equal(1, body.Data.Active);
    }

    [Fact]
    public async Task ACompetitorsCustomersNeverAppear()
    {
        var mineOwner = await IsolatedOwner();
        var theirVenue = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var theirs = await NewCustomer(_fx.OwnerBId, "Competitor Customer");
        await Seed(theirVenue.Id, theirs.Id, MonthStart.AddDays(1));

        var report = await Report(mineOwner);

        Assert.Equal(0, report.TotalCustomers);
        Assert.Empty(report.TopCustomers);
    }

    [Fact]
    public async Task Player_IsRefused()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.GetAsync("/api/v1/customers/report");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
