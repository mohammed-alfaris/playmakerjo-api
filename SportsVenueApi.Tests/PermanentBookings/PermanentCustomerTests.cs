using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.PermanentBookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.PermanentBookings;

/// <summary>
/// Standing weekly groups are roughly 40% of a pitch's bookings, and not one of their
/// organisers was in the customer book. The column and its index had been there from the
/// start; nothing ever assigned them, and there was no navigation property, so no query
/// could have loaded one even if it had.
///
/// The effect: the owner's most valuable relationships — the people who come every single
/// week — were the only ones with no record, no phone, no attendance history, and no way to
/// notice when one quietly stopped coming.
/// </summary>
[Collection("Api")]
public class PermanentCustomerTests
{
    private readonly DatabaseFixture _fx;

    public PermanentCustomerTests(DatabaseFixture fx) => _fx = fx;

    private async Task<PermanentBookingDto> Create(HttpClient client, string venueId, object body)
    {
        var res = await client.PostAsJsonAsync($"/api/v1/venues/{venueId}/permanent-bookings", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<PermanentBookingDto>>())!.Data!;
    }

    [Fact]
    public async Task TheOrganiserIsCapturedIntoTheCustomerBook()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var dto = await Create(owner, venue.Id, new
        {
            dayOfWeek = 2, startTime = "19:00", duration = 60,
            label = "Khalid weekly",
            customerPhone = "0791235001", customerName = "خالد النتور",
        });

        Assert.NotNull(dto.Customer);
        Assert.Equal("خالد النتور", dto.Customer!.Name);
        Assert.Equal("+962791235001", dto.Customer.Phone);

        // And he is a real customer, reachable from the book like any other.
        var found = await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .GetAsync("/api/v1/customers/lookup?phone=0791235001");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Contains("خالد النتور", await found.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheSamePersonBookingBothWaysIsOneCustomer()
    {
        // The whole point of keying on (owner, phone). A regular who has a standing Tuesday
        // AND walks in on a Friday must be one row, or his history is split in half.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        await Create(owner, venue.Id, new
        {
            dayOfWeek = 3, startTime = "20:00", duration = 60,
            customerPhone = "0791235002", customerName = "سامي",
        });

        var walkIn = await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball",
            date = SportsVenueApi.Constants.PlatformConstants.JordanToday().AddDays(9).ToString("yyyy-MM-dd"),
            startTime = "15:00", duration = 60, paymentMethod = "cliq",
            isManual = true, customerPaid = true,
            customerPhone = "+962791235002", customerName = "سامي",
        });
        Assert.Equal(HttpStatusCode.OK, walkIn.StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var count = await db.Customers.CountAsync(c => c.OwnerId == _fx.OwnerAId && c.Phone == "+962791235002");

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task AStandingBookingWithNoPhoneStillWorks()
    {
        // Optional, exactly like a walk-in. Refusing the booking over a missing number would
        // teach the counter to type 0000000000, and then the book is worse than empty.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var dto = await Create(owner, venue.Id, new
        {
            dayOfWeek = 4, startTime = "21:00", duration = 60, label = "Friday lads",
        });

        Assert.Null(dto.Customer);
        Assert.Equal("Friday lads", dto.Label);
    }

    [Fact]
    public async Task AnUnusablePhoneKeepsTheBookingAndRecordsNoCustomer()
    {
        // A landline. The reservation is real and must stand; only the customer is skipped.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var dto = await Create(owner, venue.Id, new
        {
            dayOfWeek = 5, startTime = "18:00", duration = 60,
            customerPhone = "064811222", customerName = "مكتب الشركة",
        });

        Assert.Null(dto.Customer);
        Assert.Equal("active", dto.Status);
    }

    [Fact]
    public async Task TheOrganiserSurvivesOnTheListEndpoint()
    {
        // The timeline reads the list, not the create response — an Include missing there
        // would show the customer once and never again.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        await Create(owner, venue.Id, new
        {
            dayOfWeek = 1, startTime = "17:00", duration = 60,
            customerPhone = "0791235003", customerName = "ليث",
        });

        var res = await owner.GetAsync($"/api/v1/venues/{venue.Id}/permanent-bookings?status=active");
        var list = (await res.Content.ReadFromJsonAsync<ApiResponse<List<PermanentBookingDto>>>())!.Data!;

        Assert.Equal("ليث", Assert.Single(list).Customer!.Name);
    }
}
