using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Staff;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// Owners as companies: roles they define, and staff limited to the venues they work at.
///
/// Every test builds its own company — a fresh owner with their own venues — so nothing here
/// depends on, or disturbs, the fixture's shared owners and clerks.
/// </summary>
[Collection("Api")]
public class StaffRolesTests
{
    private readonly DatabaseFixture _fx;

    public StaffRolesTests(DatabaseFixture fx) => _fx = fx;

    private static string Day(int ahead) => PlatformConstants.JordanToday().AddDays(ahead).ToString("yyyy-MM-dd");

    private sealed record Company(User Owner, HttpClient Client, Venue VenueA, Venue VenueB);

    private async Task<Company> NewCompany()
    {
        var owner = await _fx.CreateOwner();
        var a = await _fx.CreateBasketballVenue(owner.Id, v => v.Name = "Branch A");
        var b = await _fx.CreateBasketballVenue(owner.Id, v => v.Name = "Branch B");
        return new Company(owner, _fx.CreateClientFor(owner.Id, "venue_owner"), a, b);
    }

    private static async Task<StaffRoleResponse> CreateRole(HttpClient owner, string name, params string[] permissions)
    {
        var res = await owner.PostAsJsonAsync("/api/v1/staff-roles", new { name, permissions });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<StaffRoleResponse>>())!.Data!;
    }

    /// <summary>Hire a clerk through the API, exactly as the Team page does.</summary>
    private async Task<(UserResponse Clerk, HttpClient Client)> Hire(
        HttpClient owner, string? roleId = null, string[]? venueIds = null)
    {
        var email = $"clerk-{Guid.NewGuid():N}@test.local";
        var res = await owner.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Clerk",
            email,
            password = DatabaseFixture.TestPassword,
            role = "venue_staff",
            staffRoleId = roleId,
            allVenues = venueIds == null ? (bool?)null : false,
            venueIds,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var clerk = (await res.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;
        return (clerk, await _fx.CreateClientForUserAsync(clerk.Id));
    }

    private static object Manual(string venueId, string time, bool customerPaid = true) => new
    {
        venueId, sport = "basketball", date = Day(12), startTime = time,
        duration = 60, paymentMethod = "cliq", isManual = true, customerPaid,
    };

    private static async Task<BookingResponse> Book(
        HttpClient client, string venueId, string time, bool customerPaid = true)
    {
        var res = await client.PostAsJsonAsync("/api/v1/bookings", Manual(venueId, time, customerPaid));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!;
    }

    // ------------------------------------------------------------------ roles

    [Fact]
    public async Task ANewCompany_StartsWithTheTwoStarterRoles()
    {
        var co = await NewCompany();

        var res = await co.Client.GetAsync("/api/v1/staff-roles");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var roles = (await res.Content.ReadFromJsonAsync<ApiResponse<List<StaffRoleResponse>>>())!.Data!;
        Assert.Equal(["Front desk", "View only"], roles.Select(r => r.Name));
        Assert.Equal(StaffPermissions.LegacyRead, roles.Single(r => r.Name == "View only").Permissions);
    }

    [Fact]
    public async Task ARoleWithAnUnknownPermission_IsRefused()
    {
        var co = await NewCompany();

        var res = await co.Client.PostAsJsonAsync("/api/v1/staff-roles",
            new { name = "Rogue", permissions = new[] { "venues.delete" } });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ADuplicateRoleName_IsRefused()
    {
        var co = await NewCompany();

        var res = await co.Client.PostAsJsonAsync("/api/v1/staff-roles",
            new { name = "Front desk", permissions = new[] { StaffPermissions.BookingsView } });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task AManagePermission_BringsItsViewWithIt()
    {
        var co = await NewCompany();

        var role = await CreateRole(co.Client, "Editor", StaffPermissions.CustomersManage);

        Assert.Equal([StaffPermissions.CustomersView, StaffPermissions.CustomersManage], role.Permissions);
    }

    [Fact]
    public async Task AnotherCompanysRole_CannotBeEditedOrAssigned()
    {
        var mine = await NewCompany();
        var theirs = await NewCompany();
        var theirRole = await CreateRole(theirs.Client, "Theirs", StaffPermissions.BookingsView);

        var edit = await mine.Client.PatchAsJsonAsync($"/api/v1/staff-roles/{theirRole.Id}", new { name = "Mine now" });
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);

        var hire = await mine.Client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Clerk", email = $"x-{Guid.NewGuid():N}@test.local", password = DatabaseFixture.TestPassword,
            role = "venue_staff", staffRoleId = theirRole.Id,
        });
        Assert.Equal(HttpStatusCode.BadRequest, hire.StatusCode);
    }

    [Fact]
    public async Task ARoleInUse_CannotBeDeleted_AnUnusedOneCan()
    {
        var co = await NewCompany();
        var used = await CreateRole(co.Client, "Used", StaffPermissions.BookingsView);
        var unused = await CreateRole(co.Client, "Unused", StaffPermissions.BookingsView);
        await Hire(co.Client, used.Id);

        Assert.Equal(HttpStatusCode.Conflict, (await co.Client.DeleteAsync($"/api/v1/staff-roles/{used.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await co.Client.DeleteAsync($"/api/v1/staff-roles/{unused.Id}")).StatusCode);
    }

    [Fact]
    public async Task Staff_CannotManageRoles_WhateverTheirRole()
    {
        var co = await NewCompany();
        var everything = await CreateRole(co.Client, "Everything", StaffPermissions.All.ToArray());
        var (_, clerk) = await Hire(co.Client, everything.Id);

        var res = await clerk.PostAsJsonAsync("/api/v1/staff-roles",
            new { name = "Self-promotion", permissions = new[] { StaffPermissions.ReportsView } });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task HiringWithoutARole_GivesViewOnly()
    {
        var co = await NewCompany();

        var (clerk, _) = await Hire(co.Client);

        Assert.Equal("View only", clerk.StaffRole!.Name);
        Assert.True(clerk.AllVenues);
    }

    [Fact]
    public async Task EditingARole_AppliesOnTheClerksNextRequest_WithoutLoggingIn()
    {
        var co = await NewCompany();
        var role = await CreateRole(co.Client, "Growing", StaffPermissions.BookingsView);
        var (_, clerk) = await Hire(co.Client, role.Id);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await clerk.PostAsJsonAsync("/api/v1/bookings", Manual(co.VenueA.Id, "09:00"))).StatusCode);

        await co.Client.PatchAsJsonAsync($"/api/v1/staff-roles/{role.Id}",
            new { permissions = new[] { StaffPermissions.BookingsView, StaffPermissions.BookingsManage } });

        Assert.Equal(HttpStatusCode.OK,
            (await clerk.PostAsJsonAsync("/api/v1/bookings", Manual(co.VenueA.Id, "09:00"))).StatusCode);
    }

    // ------------------------------------------------------------------ venue scope

    [Fact]
    public async Task AClerkLimitedToOneBranch_CannotWorkTheOther()
    {
        var co = await NewCompany();
        var frontDesk = await CreateRole(co.Client, "Desk", StaffPermissions.LegacyWrite.ToArray());
        var (_, clerk) = await Hire(co.Client, frontDesk.Id, [co.VenueA.Id]);

        Assert.Equal(HttpStatusCode.OK,
            (await clerk.PostAsJsonAsync("/api/v1/bookings", Manual(co.VenueA.Id, "10:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await clerk.PostAsJsonAsync("/api/v1/bookings", Manual(co.VenueB.Id, "10:00"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await clerk.GetAsync($"/api/v1/venues/{co.VenueB.Id}/permanent-bookings")).StatusCode);
    }

    [Fact]
    public async Task AClerkLimitedToOneBranch_OnlySeesThatBranchInLists()
    {
        var co = await NewCompany();
        var bookingA = await Book(co.Client, co.VenueA.Id, "11:00");
        var bookingB = await Book(co.Client, co.VenueB.Id, "11:00");
        var desk = await CreateRole(co.Client, "Desk", StaffPermissions.LegacyWrite.ToArray());
        var (_, clerk) = await Hire(co.Client, desk.Id, [co.VenueA.Id]);

        var bookings = (await (await clerk.GetAsync("/api/v1/bookings?limit=100"))
            .Content.ReadFromJsonAsync<ApiResponse<List<BookingResponse>>>())!.Data!;
        Assert.Contains(bookings, b => b.Id == bookingA.Id);
        Assert.DoesNotContain(bookings, b => b.Id == bookingB.Id);

        var venues = (await (await clerk.GetAsync("/api/v1/venues?limit=100"))
            .Content.ReadFromJsonAsync<ApiResponse<List<VenueResponse>>>())!.Data!;
        Assert.Equal([co.VenueA.Id], venues.Select(v => v.Id));

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync($"/api/v1/bookings/{bookingB.Id}")).StatusCode);
    }

    [Fact]
    public async Task AnAllVenuesClerk_SeesABranchOpenedLater()
    {
        var co = await NewCompany();
        var (_, clerk) = await Hire(co.Client);
        var later = await _fx.CreateBasketballVenue(co.Owner.Id, v => v.Name = "Opened later");

        var venues = (await (await clerk.GetAsync("/api/v1/venues?limit=100"))
            .Content.ReadFromJsonAsync<ApiResponse<List<VenueResponse>>>())!.Data!;

        Assert.Contains(venues, v => v.Id == later.Id);
    }

    [Fact]
    public async Task AVenueFromAnotherCompany_CannotBeAssigned()
    {
        var mine = await NewCompany();
        var theirs = await NewCompany();
        var (clerk, _) = await Hire(mine.Client);

        var res = await mine.Client.PatchAsJsonAsync($"/api/v1/users/{clerk.Id}/staff",
            new { allVenues = false, venueIds = new[] { theirs.VenueA.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ------------------------------------------------------------------ one gate per permission

    [Fact]
    public async Task WithoutPaymentsRecord_AClerkCannotMarkPaid_OrCompleteABookingThatOwes()
    {
        var co = await NewCompany();
        // Booked by phone, to be paid on arrival: the balance is still owed.
        var owed = await Book(co.Client, co.VenueA.Id, "12:00", customerPaid: false);
        Assert.True(owed.AmountPaid < owed.TotalAmount);
        var schedule = await CreateRole(co.Client, "Schedule only",
            StaffPermissions.BookingsView, StaffPermissions.BookingsManage);
        var (_, clerk) = await Hire(co.Client, schedule.Id);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await clerk.PatchAsync($"/api/v1/bookings/{owed.Id}/mark-paid", null)).StatusCode);
        // Completing collects the balance, so it is refused too — otherwise it is a back door
        // to recording money.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await clerk.PatchAsync($"/api/v1/bookings/{owed.Id}/complete", null)).StatusCode);
        Assert.Equal(0, (await _fx.LoadPayments(owed.Id)).Count);
    }

    [Fact]
    public async Task CustomersView_WithoutExport_CanReadTheBookButNotDownloadIt()
    {
        var co = await NewCompany();
        var role = await CreateRole(co.Client, "Reader", StaffPermissions.CustomersView);
        var (_, clerk) = await Hire(co.Client, role.Id);

        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/v1/customers/export")).StatusCode);
    }

    [Fact]
    public async Task Reports_OpenOnlyToARoleThatGrantsThem()
    {
        var co = await NewCompany();
        var without = await CreateRole(co.Client, "No reports", StaffPermissions.BookingsView);
        var with = await CreateRole(co.Client, "Manager", StaffPermissions.ReportsView);
        var (_, plain) = await Hire(co.Client, without.Id);
        var (_, manager) = await Hire(co.Client, with.Id);

        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/v1/reports/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/v1/reports/summary")).StatusCode);
    }

    [Fact]
    public async Task WithoutPaymentsView_TheLedgerShowsNothing()
    {
        // The ledger answers an empty list, not 403, to anyone with no view of it — the same
        // shape an unlinked clerk has always had. The point is that the money is not in it.
        var co = await NewCompany();
        await Book(co.Client, co.VenueA.Id, "13:00");   // paid at the counter → one ledger row
        var role = await CreateRole(co.Client, "No money", StaffPermissions.BookingsView);
        var (_, clerk) = await Hire(co.Client, role.Id);

        var res = await clerk.GetAsync("/api/v1/payments");
        var owner = await co.Client.GetAsync("/api/v1/payments");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty((await res.Content.ReadFromJsonAsync<ApiResponse<List<object>>>())!.Data!);
        Assert.NotEmpty((await owner.Content.ReadFromJsonAsync<ApiResponse<List<object>>>())!.Data!);
    }

    // ------------------------------------------------------------------ what the dashboard is told

    [Fact]
    public async Task Me_TellsAClerkExactlyWhatTheyMayDo()
    {
        var co = await NewCompany();
        var role = await CreateRole(co.Client, "Cashier", StaffPermissions.PaymentsRecord);
        var (_, clerk) = await Hire(co.Client, role.Id, [co.VenueA.Id]);

        var me = (await (await clerk.GetAsync("/api/v1/users/me"))
            .Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;

        Assert.NotNull(me.Access);
        Assert.Equal(co.Owner.Id, me.Access!.CompanyId);
        Assert.Equal("Cashier", me.Access.StaffRole!.Name);
        Assert.Equal([StaffPermissions.PaymentsView, StaffPermissions.PaymentsRecord], me.Access.Permissions);
        Assert.False(me.Access.AllVenues);
        Assert.Equal([co.VenueA.Id], me.Access.VenueIds);
    }
}
