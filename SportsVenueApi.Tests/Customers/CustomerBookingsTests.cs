using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Customers;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Customers;

/// <summary>
/// GET /api/v1/customers/{id}/bookings — the detail page's full history table. Distinct from
/// <c>Get</c>'s <c>recentBookings</c>, which is a capped preview for callers that just want a
/// quick glance.
/// </summary>
[Collection("Api")]
public class CustomerBookingsTests
{
    private readonly DatabaseFixture _fx;

    public CustomerBookingsTests(DatabaseFixture fx) => _fx = fx;

    private static DateTime Today => PlatformConstants.JordanToday();

    private async Task<Customer> NewCustomer(string ownerId)
    {
        return await _fx.Insert(new Customer
        {
            OwnerId = ownerId,
            Phone = "+96279" + Random.Shared.Next(1000000, 9999999),
            Name = "History Subject",
        });
    }

    private async Task Seed(string venueId, string customerId, int daysAgo, string status, string? notes = null)
    {
        await _fx.Insert(new Booking
        {
            VenueId = venueId,
            PlayerId = _fx.PlayerId,
            CustomerId = customerId,
            Sport = "basketball",
            Date = Today.AddDays(-daysAgo),
            StartTime = "10:00",
            Duration = 60,
            Amount = 20,
            TotalAmount = 20,
            AmountPaid = 20,
            Status = status,
            IsManual = true,
            Notes = notes,
        });
    }

    [Fact]
    public async Task ReturnsEveryBooking_NotJustARecentSlice()
    {
        // Get's `recentBookings` caps at 50. A customer with 60 visits must still be able to
        // see all of them on the detail page, just paginated rather than truncated.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        for (var i = 1; i <= 55; i++)
            await Seed(venue.Id, customer.Id, daysAgo: i, "completed");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/{customer.Id}/bookings?page=1&limit=20");
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<List<CustomerBookingItem>>>();

        Assert.Equal(55, body!.Pagination!.Total);
        Assert.Equal(20, body.Data!.Count);
    }

    [Fact]
    public async Task OrdersMostRecentFirst()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 10, "completed");
        await Seed(venue.Id, customer.Id, daysAgo: 1, "completed");
        await Seed(venue.Id, customer.Id, daysAgo: 5, "completed");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/{customer.Id}/bookings");
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<List<CustomerBookingItem>>>();

        Assert.Equal(
            [Today.AddDays(-1).ToString("yyyy-MM-dd"), Today.AddDays(-5).ToString("yyyy-MM-dd"), Today.AddDays(-10).ToString("yyyy-MM-dd")],
            body!.Data!.Select(b => b.Date));
    }

    [Fact]
    public async Task CarriesTheBookingsOwnNote()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 2, "completed", notes: "Paid the balance in cash");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/{customer.Id}/bookings");
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<List<CustomerBookingItem>>>();

        Assert.Equal("Paid the balance in cash", body!.Data!.Single().Notes);
    }

    [Fact]
    public async Task FiltersByStatus()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var customer = await NewCustomer(_fx.OwnerAId);
        await Seed(venue.Id, customer.Id, daysAgo: 3, "completed");
        await Seed(venue.Id, customer.Id, daysAgo: 4, "no_show");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/{customer.Id}/bookings?status=no_show");
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<List<CustomerBookingItem>>>();

        Assert.Equal(1, body!.Pagination!.Total);
        Assert.Equal("no_show", body.Data!.Single().Status);
    }

    [Fact]
    public async Task ACompetitorsCustomerHistory_CannotBeRead()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var theirs = await NewCustomer(_fx.OwnerBId);
        await Seed(venue.Id, theirs.Id, daysAgo: 1, "completed");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/customers/{theirs.Id}/bookings");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
