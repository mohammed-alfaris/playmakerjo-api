using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Companies;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Billing;

/// <summary>
/// The admin's Suspend switch: the company's owner and staff keep their sign-in but lose the
/// back office, its venues leave the app, and lifting it puts everything back.
/// </summary>
[Collection("Api")]
public class SuspensionTests
{
    private readonly DatabaseFixture _fx;
    private readonly HttpClient _admin;

    public SuspensionTests(DatabaseFixture fx)
    {
        _fx = fx;
        _admin = fx.CreateClientFor(fx.AdminId, "super_admin");
    }

    private static string Day(int ahead) => PlatformConstants.JordanToday().AddDays(ahead).ToString("yyyy-MM-dd");

    private Task<HttpResponseMessage> SetSuspended(string ownerId, bool suspended) =>
        _admin.PatchAsJsonAsync($"/api/v1/companies/{ownerId}/suspension", new { suspended, reason = "Invoice PMJ-2026-0001 unpaid" });

    [Fact]
    public async Task ASuspendedCompany_LosesItsBackOffice_ButCanStillSeeWhy()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var staff = await _fx.CreateStaff(owner.Id, "write");
        var ownerClient = _fx.CreateClientFor(owner.Id, "venue_owner");
        var staffClient = await _fx.CreateClientForUserAsync(staff.Id);
        Assert.Equal(HttpStatusCode.OK, (await SetSuspended(owner.Id, true)).StatusCode);

        // No bookings taken, none listed.
        var counter = await ownerClient.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Day(3), startTime = "10:00", duration = 60,
            paymentMethod = "cash", isManual = true,
        });
        Assert.NotEqual(HttpStatusCode.OK, counter.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await staffClient.PostAsJsonAsync($"/api/v1/venues/{venue.Id}/blocks", new { startsAt = $"{Day(3)}T10:00", endsAt = $"{Day(3)}T11:00" })).StatusCode);

        // The owner's own back office is closed too, not just the venues' booking door.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ownerClient.PostAsJsonAsync($"/api/v1/venues/{venue.Id}/blocks", new { startsAt = $"{Day(3)}T10:00", endsAt = $"{Day(3)}T11:00" })).StatusCode);
        var ownerMe = (await (await ownerClient.GetAsync("/api/v1/users/me")).Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;
        Assert.True(ownerMe.Access!.CompanySuspended);

        // Both are told why.
        var me = (await (await staffClient.GetAsync("/api/v1/users/me")).Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;
        Assert.True(me.Access!.CompanySuspended);
        var company = (await (await ownerClient.GetAsync("/api/v1/companies/me")).Content.ReadFromJsonAsync<ApiResponse<CompanyResponse>>())!.Data!;
        Assert.Equal(("suspended", "Invoice PMJ-2026-0001 unpaid"), (company.Billing.Status, company.Billing.SuspendedReason));
        // And the owner can still open their invoices.
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync("/api/v1/invoices")).StatusCode);
    }

    [Fact]
    public async Task ASuspendedCompanysVenues_LeaveTheApp()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        await SetSuspended(owner.Id, true);

        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync($"/api/v1/venues/public/{venue.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await player.GetAsync($"/api/v1/venues/{venue.Id}/available-slots?date={Day(3)}")).StatusCode);
        var list = (await (await player.GetAsync("/api/v1/venues/public?limit=100")).Content.ReadFromJsonAsync<ApiResponse<List<VenueResponse>>>())!.Data!;
        Assert.DoesNotContain(list, v => v.Id == venue.Id);
        var booking = await player.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Day(3), startTime = "10:00", duration = 60, paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.BadRequest, booking.StatusCode);
    }

    [Fact]
    public async Task LiftingTheSuspension_PutsEverythingBack()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        await SetSuspended(owner.Id, true);
        Assert.Equal(HttpStatusCode.OK, (await SetSuspended(owner.Id, false)).StatusCode);

        var counter = await _fx.CreateClientFor(owner.Id, "venue_owner").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Day(3), startTime = "10:00", duration = 60,
            paymentMethod = "cash", isManual = true,
        });
        Assert.Equal(HttpStatusCode.OK, counter.StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _fx.CreateClientFor(_fx.PlayerId, "player").GetAsync($"/api/v1/venues/public/{venue.Id}")).StatusCode);
    }

    [Fact]
    public async Task OnlyTheAdminSuspends()
    {
        var owner = await _fx.CreateOwner();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _fx.CreateClientFor(owner.Id, "venue_owner").PatchAsJsonAsync($"/api/v1/companies/{owner.Id}/suspension", new { suspended = false })).StatusCode);
    }
}
