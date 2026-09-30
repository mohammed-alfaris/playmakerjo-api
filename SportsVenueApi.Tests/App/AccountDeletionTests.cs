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

namespace SportsVenueApi.Tests.App;

/// <summary>
/// A player deleting their own account from the app — a store requirement. Personal details
/// go; upcoming bookings are cancelled under each venue's rule; the venues' books and the
/// payment ledger stay whole.
/// </summary>
[Collection("Api")]
public class AccountDeletionTests
{
    private readonly DatabaseFixture _fx;

    public AccountDeletionTests(DatabaseFixture fx) => _fx = fx;

    private static string Day(int ahead) => PlatformConstants.JordanToday().AddDays(ahead).ToString("yyyy-MM-dd");

    private static Task<HttpResponseMessage> Delete(HttpClient client, string? password) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/users/me") { Content = JsonContent.Create(new { password }) });

    [Fact]
    public async Task DeletingCancelsUpcomingBookingsByTheRule_AndForgetsThePerson()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");
        var ownerClient = _fx.CreateClientFor(owner.Id, "venue_owner");

        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Day(5), startTime = "18:00", duration = 60, paymentMethod = "cliq",
        });
        var bookingId = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
        await ownerClient.PatchAsync($"/api/v1/bookings/{bookingId}/mark-paid", null);
        await client.PostAsync($"/api/v1/favorites/{venue.Id}", null);

        Assert.Equal(HttpStatusCode.OK, (await Delete(client, DatabaseFixture.TestPassword)).StatusCode);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var booking = await db.Bookings.AsNoTracking().FirstAsync(b => b.Id == bookingId);
        Assert.Equal(("cancelled", 0.0), (booking.Status, booking.AmountPaid)); // 5 days out: refunded by the 24h rule
        var rows = await db.Payments.AsNoTracking().Where(p => p.BookingId == bookingId).ToListAsync();
        Assert.Equal(booking.AmountPaid, rows.Sum(p => p.Amount), 3);

        var gone = await db.Users.AsNoTracking().FirstAsync(u => u.Id == player.Id);
        Assert.Equal(("deleted", "Deleted user", (string?)null), (gone.Status, gone.Name, gone.Phone));
        Assert.NotEqual(player.Email, gone.Email);
        Assert.False(await db.Favorites.AnyAsync(f => f.UserId == player.Id));
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == owner.Id && n.Type == "booking_cancelled" && n.ReferenceId == bookingId));

        // The old address signs in nowhere, and can be used for a new account.
        var login = await _fx.Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { email = player.Email, password = DatabaseFixture.TestPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var again = await _fx.Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { name = "New Me", email = player.Email, password = "another-pass-1" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task TheWrongPassword_DeletesNothing()
    {
        var player = await _fx.CreatePlayer();

        var res = await Delete(_fx.CreateClientFor(player.Id, "player"), "not-my-password");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("active", (await _fx.LoadUser(player.Id))!.Status);
    }

    [Fact]
    public async Task AGoogleOnlyAccount_NeedsNoPassword()
    {
        var player = await _fx.CreatePlayer();
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var u = await db.Users.FirstAsync(x => x.Id == player.Id);
            u.PasswordHash = string.Empty;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await Delete(_fx.CreateClientFor(player.Id, "player"), null)).StatusCode);
    }

    [Fact]
    public async Task BusinessAccounts_AreClosedByPlayMaker_NotFromTheApp()
    {
        var owner = await _fx.CreateOwner();

        Assert.Equal(HttpStatusCode.BadRequest, (await Delete(_fx.CreateClientFor(owner.Id, "venue_owner"), DatabaseFixture.TestPassword)).StatusCode);
        Assert.Equal("active", (await _fx.LoadUser(owner.Id))!.Status);
    }

    [Fact]
    public async Task ABookingShowsWhatWasRefundedOnIt()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var ownerClient = _fx.CreateClientFor(owner.Id, "venue_owner");
        var res = await ownerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Day(4), startTime = "10:00", duration = 60,
            paymentMethod = "cash", isManual = true, customerPaid = true, customerPhone = "0791231234", customerName = "R",
        });
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var before = (await (await ownerClient.GetAsync($"/api/v1/bookings/{id}")).Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!;
        await ownerClient.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 5, kind = "refund" });
        var after = (await (await ownerClient.GetAsync($"/api/v1/bookings/{id}")).Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!;

        Assert.Null(before.RefundedAmount);
        Assert.Equal(5, after.RefundedAmount);
    }
}
