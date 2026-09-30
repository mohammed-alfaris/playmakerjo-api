using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Leads;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Billing;

[Collection("Api")]
public class LeadsAndOnboardingTests
{
    private readonly DatabaseFixture _fx;
    private readonly HttpClient _admin;

    public LeadsAndOnboardingTests(DatabaseFixture fx)
    {
        _fx = fx;
        _admin = fx.CreateClientFor(fx.AdminId, "super_admin");
    }

    private async Task<int> NewLead()
    {
        var anon = _fx.Factory.CreateClient();
        var res = await anon.PostAsJsonAsync("/api/v1/waitlist/venue", new
        {
            contactName = "Lead Person", venueName = "Lead Arena " + Guid.NewGuid().ToString("N")[..6], city = "Amman",
            phone = "0791112233", email = $"lead-{Guid.NewGuid():N}@test.local", sports = new[] { "football" },
        });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return db.VenueWaitlist.OrderByDescending(v => v.Id).First().Id;
    }

    private async Task<VenueLeadResponse> Patch(int id, object body)
    {
        var res = await _admin.PatchAsJsonAsync($"/api/v1/waitlist/venues/{id}", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<VenueLeadResponse>>())!.Data!;
    }

    [Fact]
    public async Task ALead_MovesThroughTheStages_WithNotesAndAFollowUp()
    {
        var id = await NewLead();
        var today = PlatformConstants.JordanToday().ToString("yyyy-MM-dd");

        var lead = await Patch(id, new { status = "contacted", notes = "Wants a demo on Sunday", nextFollowUpOn = today });

        Assert.Equal(("contacted", "Wants a demo on Sunday", today, true), (lead.Status, lead.Notes, lead.NextFollowUpOn, lead.FollowUpDue));
        var due = (await (await _admin.GetAsync("/api/v1/waitlist/venues?status=due&limit=200"))
            .Content.ReadFromJsonAsync<ApiResponse<List<VenueLeadResponse>>>())!.Data!;
        Assert.Contains(due, l => l.Id == id);

        var lost = await Patch(id, new { status = "lost", lostReason = "Too expensive" });
        Assert.Equal(("lost", "Too expensive", (string?)null), (lost.Status, lost.LostReason, lost.NextFollowUpOn));

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PatchAsJsonAsync($"/api/v1/waitlist/venues/{id}", new { status = "maybe" })).StatusCode);
    }

    [Fact]
    public async Task LinkingTheOwnerItBecame_MarksTheLeadWon()
    {
        var id = await NewLead();
        var owner = await _fx.CreateOwner();

        var won = await Patch(id, new { convertedOwnerId = owner.Id });

        Assert.Equal(("won", owner.Id), (won.Status, won.ConvertedOwnerId));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PatchAsJsonAsync($"/api/v1/waitlist/venues/{id}", new { convertedOwnerId = _fx.PlayerId })).StatusCode);
    }

    [Fact]
    public async Task StageCounts_AndOnlyTheAdminSeesLeads()
    {
        await NewLead();
        var stats = (await (await _admin.GetAsync("/api/v1/waitlist/venues/stats")).Content.ReadFromJsonAsync<ApiResponse<LeadStats>>())!.Data!;
        Assert.True(stats.ByStatus["new"] >= 1);
        Assert.Equal(6, stats.ByStatus.Count);

        var owner = await _fx.CreateOwner();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _fx.CreateClientFor(owner.Id, "venue_owner").GetAsync("/api/v1/waitlist/venues")).StatusCode);
    }

    [Fact]
    public async Task Onboarding_FollowsWhatTheCompanyHasActuallyDone()
    {
        var owner = await _fx.CreateOwner();
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");
        async Task<Dictionary<string, bool>> Steps() =>
            (await (await client.GetAsync("/api/v1/companies/me/onboarding")).Content.ReadFromJsonAsync<ApiResponse<OnboardingResponse>>())!
                .Data!.Steps.ToDictionary(s => s.Key, s => s.Done);

        var empty = await Steps();
        Assert.All(empty.Values, Assert.False);
        Assert.Equal(8, empty.Count);

        var venue = await _fx.CreateBasketballVenue(owner.Id);
        await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = PlatformConstants.JordanToday().AddDays(2).ToString("yyyy-MM-dd"),
            startTime = "10:00", duration = 60, paymentMethod = "cash", isManual = true,
            customerPhone = "0795556677", customerName = "First Customer",
        });

        var after = await Steps();
        Assert.True(after["venue"] && after["pitches"] && after["hours"] && after["cliq"] && after["first_booking"] && after["customer"]);
        Assert.False(after["staff"] || after["standing"]);

        // The admin sees the same list for any company.
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync($"/api/v1/companies/{owner.Id}/onboarding")).StatusCode);
    }
}
