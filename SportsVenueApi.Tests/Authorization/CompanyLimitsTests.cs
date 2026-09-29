using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Companies;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// The platform's limits on each company: how many venues, how many active staff.
///
/// Hitting a limit refuses the new venue or clerk with a message. Lowering a limit below what a
/// company already has switches nothing off. And the limit has to hold under a double-click —
/// two creates at max−1 must not both get in, which is what the company row lock is for.
/// </summary>
[Collection("Api")]
public class CompanyLimitsTests
{
    private readonly DatabaseFixture _fx;

    public CompanyLimitsTests(DatabaseFixture fx) => _fx = fx;

    private HttpClient Admin => _fx.CreateClientFor(_fx.AdminId, "super_admin");

    private static object NewVenue(string? ownerId = null) => new
    {
        name = "Limit Venue " + Guid.NewGuid().ToString("N")[..6],
        city = "Amman", address = "Limits Street 1", pricePerHour = 20,
        sports = new[] { "basketball" }, status = "active", owner_id = ownerId,
    };

    private static object NewClerk() => new
    {
        name = "Clerk", email = $"lim-{Guid.NewGuid():N}@test.local",
        password = DatabaseFixture.TestPassword, role = "venue_staff",
    };

    private async Task SetLimits(string ownerId, int? maxVenues, int? maxStaff)
    {
        var res = await Admin.PatchAsJsonAsync($"/api/v1/companies/{ownerId}",
            new { limits = new { maxVenues, maxStaff } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static async Task<CompanyResponse> Company(HttpClient client, string path) =>
        (await (await client.GetAsync(path)).Content.ReadFromJsonAsync<ApiResponse<CompanyResponse>>())!.Data!;

    private async Task<(User Owner, HttpClient Client)> OwnerWithVenues(int venues)
    {
        var owner = await _fx.CreateOwner();
        for (var i = 0; i < venues; i++) await _fx.CreateBasketballVenue(owner.Id);
        return (owner, _fx.CreateClientFor(owner.Id, "venue_owner"));
    }

    // ------------------------------------------------------------------ venues

    [Fact]
    public async Task AtTheVenueLimit_ANewVenueIsRefused_WithAReason()
    {
        var (owner, client) = await OwnerWithVenues(2);
        await SetLimits(owner.Id, maxVenues: 2, maxStaff: null);

        var res = await client.PostAsJsonAsync("/api/v1/venues", NewVenue());

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("2 venues", body!.Message);
    }

    [Fact]
    public async Task UnderTheVenueLimit_AndWithNoLimit_CreatingWorks()
    {
        var (owner, client) = await OwnerWithVenues(1);
        await SetLimits(owner.Id, maxVenues: 2, maxStaff: null);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/venues", NewVenue())).StatusCode);

        await SetLimits(owner.Id, maxVenues: null, maxStaff: null);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/venues", NewVenue())).StatusCode);
    }

    [Fact]
    public async Task LoweringTheLimitBelowUsage_SwitchesNothingOff()
    {
        var (owner, client) = await OwnerWithVenues(3);
        await SetLimits(owner.Id, maxVenues: 1, maxStaff: null);

        var venues = (await (await client.GetAsync("/api/v1/venues?limit=100"))
            .Content.ReadFromJsonAsync<ApiResponse<List<VenueResponse>>>())!.Data!;
        Assert.Equal(3, venues.Count);
        Assert.All(venues, v => Assert.Equal("active", v.Status));

        var usage = await Company(client, "/api/v1/companies/me");
        Assert.Equal(3, usage.Venues.Used);
        Assert.Equal(1, usage.Venues.Max);

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/v1/venues", NewVenue())).StatusCode);
    }

    [Fact]
    public async Task AnAdminCannotMoveAVenueIntoACompanyThatIsFull()
    {
        var (full, _) = await OwnerWithVenues(1);
        await SetLimits(full.Id, maxVenues: 1, maxStaff: null);
        var (other, _) = await OwnerWithVenues(0);
        var venue = await _fx.CreateBasketballVenue(other.Id);

        var res = await Admin.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new { owner_id = full.Id });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(other.Id, (await _fx.LoadVenue(venue.Id))!.OwnerId);
    }

    [Fact]
    public async Task AnAdminCannotGiveAVenueToSomeoneWhoIsNotAnOwner()
    {
        var res = await Admin.PostAsJsonAsync("/api/v1/venues", NewVenue(_fx.PlayerId));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ParallelCreatesAtTheLastSeat_OnlyOneGetsIn()
    {
        // A double-click, or two tabs. Without the lock each request counts, sees one seat
        // left, and inserts — ending the company over its limit.
        var (owner, client) = await OwnerWithVenues(2);
        await SetLimits(owner.Id, maxVenues: 3, maxStaff: null);

        var gate = new TaskCompletionSource();
        var attempts = Enumerable.Range(0, 5).Select(async _ =>
        {
            var c = _fx.CreateClientFor(owner.Id, "venue_owner");
            await gate.Task;
            return (await c.PostAsJsonAsync("/api/v1/venues", NewVenue())).StatusCode;
        }).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r == HttpStatusCode.OK));
        Assert.Equal(4, results.Count(r => r == HttpStatusCode.Conflict));
        Assert.Equal(3, (await Company(client, "/api/v1/companies/me")).Venues.Used);
    }

    // ------------------------------------------------------------------ staff

    [Fact]
    public async Task AtTheStaffLimit_HiringIsRefused()
    {
        var (owner, client) = await OwnerWithVenues(1);
        await SetLimits(owner.Id, maxVenues: null, maxStaff: 1);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/users", NewClerk())).StatusCode);

        var res = await client.PostAsJsonAsync("/api/v1/users", NewClerk());

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task SuspendingFreesASeat_ButReactivatingMustFitAgain()
    {
        var (owner, client) = await OwnerWithVenues(1);
        await SetLimits(owner.Id, maxVenues: null, maxStaff: 1);
        var first = (await (await client.PostAsJsonAsync("/api/v1/users", NewClerk()))
            .Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;

        await client.PatchAsJsonAsync($"/api/v1/users/{first.Id}/status", new { status = "banned" });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/users", NewClerk())).StatusCode);

        // The replacement now holds the only seat; bringing the first one back would be two.
        var back = await client.PatchAsJsonAsync($"/api/v1/users/{first.Id}/status", new { status = "active" });
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal("banned", (await _fx.LoadUser(first.Id))!.Status);
    }

    // ------------------------------------------------------------------ who sets what

    [Fact]
    public async Task AnOwnerSeesTheirUsage_ButCannotChangeTheirLimits()
    {
        var (owner, client) = await OwnerWithVenues(2);
        await SetLimits(owner.Id, maxVenues: 5, maxStaff: 3);

        var mine = await Company(client, "/api/v1/companies/me");
        Assert.Equal(2, mine.Venues.Used);
        Assert.Equal(5, mine.Venues.Max);
        Assert.Equal(3, mine.Staff.Max);

        var raise = await client.PatchAsJsonAsync("/api/v1/companies/me",
            new { limits = new { maxVenues = 99, maxStaff = 99 } });
        Assert.Equal(HttpStatusCode.Forbidden, raise.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PatchAsJsonAsync($"/api/v1/companies/{owner.Id}", new { limits = new { maxVenues = 99 } })).StatusCode);
    }

    [Fact]
    public async Task AnOwnerCanRenameTheirCompany_StaffCannotReadIt()
    {
        var (owner, client) = await OwnerWithVenues(1);
        var rename = await client.PatchAsJsonAsync("/api/v1/companies/me", new { name = "Shabab Sports", nameAr = "شباب الرياضية" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
        Assert.Equal("شباب الرياضية", (await Company(client, "/api/v1/companies/me")).NameAr);

        var clerk = await _fx.CreateStaff(owner.Id, "write");
        var clerkClient = await _fx.CreateClientForUserAsync(clerk.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerkClient.GetAsync("/api/v1/companies/me")).StatusCode);
    }

    [Fact]
    public async Task ANewCompany_TakesThePlatformDefault_AndKeepsItWhenTheDefaultChanges()
    {
        try
        {
            await Admin.PatchAsJsonAsync("/api/v1/settings", new { defaultLimits = new { maxVenues = 3, maxStaff = 2 } });
            var created = await Admin.PostAsJsonAsync("/api/v1/users", new
            {
                name = "New Owner", email = $"own-{Guid.NewGuid():N}@test.local",
                password = DatabaseFixture.TestPassword, role = "venue_owner",
            });
            var owner = (await created.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;

            var company = await Company(Admin, $"/api/v1/companies/{owner.Id}");
            Assert.Equal(3, company.Venues.Max);
            Assert.Equal(2, company.Staff.Max);

            await Admin.PatchAsJsonAsync("/api/v1/settings", new { defaultLimits = new { maxVenues = 10, maxStaff = 10 } });
            Assert.Equal(3, (await Company(Admin, $"/api/v1/companies/{owner.Id}")).Venues.Max);
        }
        finally
        {
            // Settings are shared by the whole suite: every company created later copies them.
            await Admin.PatchAsJsonAsync("/api/v1/settings", new { defaultLimits = new { maxVenues = (int?)null, maxStaff = (int?)null } });
        }
    }

    [Fact]
    public async Task TheAdminListShowsEveryCompanyWithUsage()
    {
        var (owner, _) = await OwnerWithVenues(2);

        var res = await Admin.GetAsync($"/api/v1/companies?search={owner.Email}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var list = (await res.Content.ReadFromJsonAsync<ApiResponse<List<CompanyResponse>>>())!.Data!;
        var row = Assert.Single(list);
        Assert.Equal(owner.Id, row.Id);
        Assert.Equal(2, row.Venues.Used);
        Assert.Null(row.Venues.Max);
    }
}
