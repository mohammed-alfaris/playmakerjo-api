using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Money;

[Collection("Api")]
public class ReceiptTests
{
    private readonly DatabaseFixture _fx;

    public ReceiptTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task TheReceipt_ListsEveryLedgerRow_AndAddsUp()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = PlatformConstants.JordanToday().AddDays(2).ToString("yyyy-MM-dd"),
            startTime = "18:00", duration = 120, paymentMethod = "cash", isManual = true, customerPaid = true,
            customerPhone = "0791234567", customerName = "Receipt Customer",
        });
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
        await client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 10, kind = "refund" });

        var receipt = (await (await client.GetAsync($"/api/v1/bookings/{id}/receipt"))
            .Content.ReadFromJsonAsync<ApiResponse<BookingReceipt>>())!.Data!;

        Assert.Equal(id.ToUpperInvariant(), receipt.ReceiptNumber);
        Assert.Equal("Receipt Customer", receipt.CustomerName);
        Assert.Equal((40.0, 30.0, 10.0), (receipt.TotalAmount, receipt.AmountPaid, receipt.Balance));
        Assert.Equal(new[] { 40.0, -10.0 }, receipt.Payments.Select(p => p.Amount).ToArray());
        Assert.Equal(receipt.AmountPaid, receipt.Payments.Sum(p => p.Amount), 3);
    }

    [Fact]
    public async Task OnlyThoseWhoSeePayments_CanPrintOne()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = PlatformConstants.JordanToday().AddDays(2).ToString("yyyy-MM-dd"),
            startTime = "10:00", duration = 60, paymentMethod = "cash", isManual = true,
            customerPhone = "0791234568", customerName = "Receipt Customer",
        });
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
        var clerk = await _fx.ClerkWith(client, StaffPermissions.BookingsView, StaffPermissions.BookingsManage);

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync($"/api/v1/bookings/{id}/receipt")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _fx.CreateClientFor(_fx.PlayerId, "player").GetAsync($"/api/v1/bookings/{id}/receipt")).StatusCode);
    }
}
