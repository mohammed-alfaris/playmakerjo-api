using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Controllers;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Billing;

/// <summary>
/// The activity log: written with the change it describes, readable by the owner for their
/// company and by the admin for any, never by staff, and never edited afterwards.
/// </summary>
[Collection("Api")]
public class ActivityLogTests
{
    private readonly DatabaseFixture _fx;

    public ActivityLogTests(DatabaseFixture fx) => _fx = fx;

    private static string Day(int ahead) => PlatformConstants.JordanToday().AddDays(ahead).ToString("yyyy-MM-dd");

    private static async Task<List<ActivityItem>> Log(HttpClient client, string query = "") =>
        (await (await client.GetAsync($"/api/v1/activity?limit=200{query}")).Content.ReadFromJsonAsync<ApiResponse<List<ActivityItem>>>())!.Data!;

    private static async Task<string> PaidBooking(HttpClient client, Venue venue, string start = "18:00") =>
        (await (await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Day(5), startTime = start, duration = 120,
            paymentMethod = "cash", isManual = true, customerPaid = true,
            customerPhone = "0797778899", customerName = "Logged Customer",
        })).Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

    [Fact]
    public async Task MoneyAndScheduleChanges_AreLogged_InWords_WithWhoDidThem()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");

        var id = await PaidBooking(client, venue);
        await client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 5, kind = "refund", note = "late start" });
        await client.PatchAsJsonAsync($"/api/v1/bookings/{id}", new { startTime = "20:00" });
        await client.PatchAsJsonAsync($"/api/v1/bookings/{id}/cancel", new { refund = "none" });

        var log = await Log(client);
        Assert.Equal(new[] { "booking.cancelled", "booking.moved", "payment.refund", "booking.created" },
            log.Select(e => e.Action).ToArray());
        Assert.All(log, e => Assert.Equal((owner.Id, owner.Name, "venue_owner"), (e.OwnerId!, e.ActorName!, e.ActorRole!)));
        Assert.Contains("Refunded 5 JOD on Logged Customer", log[2].Summary);
        Assert.Contains("(late start)", log[2].Summary);
        Assert.Contains("from " + Day(5) + " 18:00", log[1].Summary);
        Assert.Contains("kept 35 JOD", log[0].Summary);
        Assert.Contains("|", log[0].Summary); // both languages
        Assert.Equal(new[] { "payment.refund" }, (await Log(client, "&area=payment")).Select(e => e.Action).ToArray());
    }

    [Fact]
    public async Task ARefusedAction_LeavesNoTrace()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");
        var id = await PaidBooking(client, venue);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/v1/bookings/{id}/refund", new { amount = 999, kind = "refund" })).StatusCode);

        Assert.DoesNotContain(await Log(client), e => e.Action.StartsWith("payment."));
    }

    [Fact]
    public async Task AVenuePriceChange_SaysFromWhatToWhat()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");

        await client.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new { pricePerHour = 25, freeCancelHours = 12 });

        var e = Assert.Single(await Log(client, "&area=venue"));
        Assert.Contains("price 20 JOD to 25 JOD/h", e.Summary);
        Assert.Contains("free cancellation 24h to 12h", e.Summary);
    }

    [Fact]
    public async Task EachCompanySeesOnlyItsOwn_StaffSeeNone_TheAdminSeesAll()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");
        var id = await PaidBooking(client, venue);
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        await admin.PatchAsJsonAsync($"/api/v1/companies/{owner.Id}/suspension", new { suspended = true, reason = "test" });
        await admin.PatchAsJsonAsync($"/api/v1/companies/{owner.Id}/suspension", new { suspended = false });

        var other = await _fx.CreateOwner();
        Assert.DoesNotContain(await Log(_fx.CreateClientFor(other.Id, "venue_owner")), e => e.EntityId == id);
        var staff = await _fx.CreateStaff(owner.Id, "write");
        Assert.Equal(HttpStatusCode.Forbidden, (await (await _fx.CreateClientForUserAsync(staff.Id)).GetAsync("/api/v1/activity")).StatusCode);

        var byAdmin = await Log(admin, $"&owner_id={owner.Id}");
        Assert.Contains(byAdmin, e => e.Action == "company.suspended" && e.ActorRole == "super_admin");
        Assert.Contains(byAdmin, e => e.Action == "booking.created" && e.CompanyName != null);
    }

    [Fact]
    public async Task TheLog_CannotBeEdited()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.AuditEvents.Add(new AuditEvent { Action = "test.event", EntityType = "test", Summary = "a|b" });
        await db.SaveChangesAsync();

        var row = await db.AuditEvents.OrderByDescending(e => e.Id).FirstAsync();
        row.Summary = "rewritten|rewritten";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }
}
