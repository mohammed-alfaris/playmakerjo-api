using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Reports;
using SportsVenueApi.DTOs.Staff;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Reports;

/// <summary>
/// The owner's reports. Every number here is asserted exactly, on a company that exists only
/// for the test and in a week in the past, so nothing else in the suite can move it.
///
/// The week of 2026-01-04 (Sunday) to 2026-01-10 (Saturday) is used throughout; 2026-01-05 is
/// a Monday.
/// </summary>
[Collection("Api")]
public class BusinessReportsTests
{
    private readonly DatabaseFixture _fx;

    public BusinessReportsTests(DatabaseFixture fx) => _fx = fx;

    private static readonly DateTime Mon = new(2026, 1, 5);

    private sealed record Co(User Owner, HttpClient Client, Venue A, Venue B);

    private async Task<Co> NewCompany()
    {
        var owner = await _fx.CreateOwner();
        var a = await _fx.CreateBasketballVenue(owner.Id, v => v.Name = "Branch A");
        var b = await _fx.CreateBasketballVenue(owner.Id, v => v.Name = "Branch B");
        return new Co(owner, _fx.CreateClientFor(owner.Id, "venue_owner"), a, b);
    }

    private async Task<Booking> Book(string venueId, DateTime date, double total, double paid = 0,
        string status = "confirmed", bool manual = true, string start = "10:00", int duration = 60,
        Action<Booking>? mutate = null)
    {
        var b = new Booking
        {
            VenueId = venueId, PlayerId = _fx.PlayerId, Sport = "basketball",
            Date = date, StartTime = start, Duration = duration,
            Amount = total, TotalAmount = total, AmountPaid = paid, OwnerAmount = total,
            Status = status, IsManual = manual,
        };
        mutate?.Invoke(b);
        return await _fx.Insert(b);
    }

    private async Task Pay(Booking b, double amount, DateTime utc, string method = "cash", string kind = "full",
        string? recordedBy = null, string? customerId = null)
    {
        await _fx.Insert(new Payment
        {
            BookingId = b.Id, PlayerId = _fx.PlayerId, VenueId = b.VenueId, CustomerId = customerId,
            Amount = amount, Method = method, Kind = kind, Status = "paid", Date = utc,
            RecordedByUserId = recordedBy,
        });
    }

    private static async Task<T> Get<T>(HttpClient client, string path)
    {
        var res = await client.GetAsync(path);
        Assert.True(res.IsSuccessStatusCode, $"{path} → {(int)res.StatusCode}");
        return (await res.Content.ReadFromJsonAsync<ApiResponse<T>>())!.Data!;
    }

    private static string Q(DateTime from, DateTime to, string extra = "") =>
        $"?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}{extra}";

    // ── Money ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Collected_IsTheLedger_OnAmmanDays()
    {
        var co = await NewCompany();
        var b = await Book(co.A.Id, Mon, total: 30, paid: 30);
        // 22:30 UTC on Monday is 01:30 on Tuesday in Amman: Tuesday's takings.
        await Pay(b, 30, Mon.AddHours(22).AddMinutes(30));

        var monday = await Get<MoneyReport>(co.Client, "/api/v1/reports/money" + Q(Mon, Mon));
        var tuesday = await Get<MoneyReport>(co.Client, "/api/v1/reports/money" + Q(Mon.AddDays(1), Mon.AddDays(1)));

        Assert.Equal(0, monday.Collected.Value);
        Assert.Equal(30, monday.Booked.Value);     // played Monday
        Assert.Equal(30, tuesday.Collected.Value); // paid Tuesday, Amman time
        Assert.Equal(0, tuesday.Booked.Value);
        Assert.Equal(30, tuesday.Daily.Single().Cash);
    }

    [Fact]
    public async Task APaidBooking_CountsWhetherOrNotItWasMarkedCompleted_AndCancelledOnesNever()
    {
        var co = await NewCompany();
        var confirmed = await Book(co.A.Id, Mon, total: 25, paid: 25, status: "confirmed");
        await Pay(confirmed, 25, Mon.AddHours(12), method: "cliq", kind: "deposit");
        await Book(co.A.Id, Mon, total: 50, status: "cancelled");

        var r = await Get<MoneyReport>(co.Client, "/api/v1/reports/money" + Q(Mon, Mon.AddDays(6)));

        Assert.Equal(25, r.Collected.Value);
        Assert.Equal(25, r.Booked.Value);
        Assert.Equal(25, r.ByMethod.Single(m => m.Key == "cliq").Amount);
        Assert.Equal(25, r.ByKind.Single(k => k.Key == "deposit").Amount);
        Assert.Equal(7, r.Daily.Count);
        var venue = r.ByVenue.Single();
        Assert.Equal(("Branch A", 25.0, 25.0, 1), (venue.Name, venue.Collected, venue.Booked, venue.Bookings));
    }

    [Fact]
    public async Task Outstanding_IsCounterBookingsPlayedAndNotPaid_WhateverThePeriod()
    {
        var co = await NewCompany();
        var customer = await _fx.Insert(new Customer
        {
            OwnerId = co.Owner.Id, Name = "Owes Money", Phone = "+96279" + Random.Shared.Next(1000000, 9999999),
        });
        await Book(co.A.Id, Mon, total: 40, paid: 10, mutate: b => b.CustomerId = customer.Id); // owes 30
        await Book(co.A.Id, Mon, total: 40, paid: 0, manual: false);                              // app: not the owner's credit
        await Book(co.A.Id, PlatformConstants.JordanToday().AddDays(3), total: 40);               // not played yet
        await Book(co.A.Id, Mon, total: 40, status: "no_show");                                   // did not come

        // A period that holds none of them: outstanding is "as of now", not per period.
        var r = await Get<MoneyReport>(co.Client, "/api/v1/reports/money" + Q(Mon.AddDays(-30), Mon.AddDays(-30)));

        Assert.Equal(30, r.Outstanding);
        Assert.Equal(1, r.OutstandingCount);
        var item = Assert.Single(r.OutstandingItems);
        Assert.Equal(("Owes Money", 30.0), (item.CustomerName, item.Owed));
    }

    [Fact]
    public async Task Compare_GivesThePreviousPeriodOfTheSameLength_AndTheFeeIsForAdminsOnly()
    {
        var co = await NewCompany();
        var thisWeek = await Book(co.A.Id, Mon, total: 20, paid: 20);
        await Pay(thisWeek, 20, Mon.AddHours(10));
        var lastWeek = await Book(co.A.Id, Mon.AddDays(-7), total: 10, paid: 10);
        await Pay(lastWeek, 10, Mon.AddDays(-7).AddHours(10));
        await Book(co.A.Id, Mon, total: 100, status: "completed", manual: false, mutate: b => b.SystemFee = 5);

        var owner = await Get<MoneyReport>(co.Client, "/api/v1/reports/money" + Q(Mon.AddDays(-1), Mon.AddDays(5), "&compare=true"));
        Assert.Equal("2025-12-28", owner.Period.PreviousFrom);
        Assert.Equal("2026-01-03", owner.Period.PreviousTo);
        Assert.Equal((20.0, 10.0), (owner.Collected.Value, owner.Collected.Previous!.Value));
        Assert.Null(owner.PlatformFee);
        Assert.Null(owner.Net);

        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var asAdmin = await Get<MoneyReport>(admin, $"/api/v1/reports/money{Q(Mon.AddDays(-1), Mon.AddDays(5))}&owner_id={co.Owner.Id}");
        Assert.Equal(5, asAdmin.PlatformFee!.Value);
        Assert.Equal(120 - 5, asAdmin.Net!.Value);
    }

    // ── Bookings health ──────────────────────────────────────────────────────

    [Fact]
    public async Task BookingsHealth_SplitsChannels_Cancellations_AndNoShows()
    {
        var co = await NewCompany();
        await Book(co.A.Id, Mon, 20, status: "completed", manual: false);                       // app, attended
        await Book(co.A.Id, Mon, 20, status: "confirmed");                                      // counter, attended (past)
        await Book(co.A.Id, Mon, 20, status: "confirmed", mutate: b => b.PermanentBookingId = "pb-x"); // weekly
        await Book(co.A.Id, Mon, 20, status: "no_show");                                        // counter no-show
        await Book(co.A.Id, Mon, 20, status: "cancelled", manual: false, mutate: b => b.AutoCancelledAt = Mon);
        await Book(co.A.Id, Mon, 20, status: "cancelled");

        var r = await Get<BookingsReport>(co.Client, "/api/v1/reports/bookings" + Q(Mon, Mon));

        Assert.Equal(4, r.Bookings.Value);
        Assert.Equal(Math.Round(2 * 100.0 / 6, 1), r.CancelRate.Value);
        Assert.Equal(3, r.Attended);
        Assert.Equal(Math.Round(1 * 100.0 / 4, 1), r.NoShowRate.Value);
        Assert.Equal((1, 1), (r.CancelledByPerson, r.CancelledExpired));
        Assert.Equal(2, r.ByChannel.Single(c => c.Key == "counter").Count);
        Assert.Equal(1, r.ByChannel.Single(c => c.Key == "app").Count);
        Assert.Equal(1, r.ByChannel.Single(c => c.Key == "weekly").Count);
        var day = Assert.Single(r.Daily);
        Assert.Equal((1, 2, 1, 0, 2), (day.App, day.Counter, day.Weekly, day.Series, day.Cancelled));
    }

    // ── Busy hours ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Occupancy_CountsAHalfOfASubdividablePitchAsHalf()
    {
        var owner = await _fx.CreateOwner();
        var (venue, pitchId) = await _fx.CreateSubdividableVenue(owner.Id); // 11-aside, 08:00–23:00
        await Book(venue.Id, Mon, 20, start: "10:00", mutate: b => { b.PitchId = pitchId; b.PitchSize = "8"; b.Sport = "football"; });
        await Book(venue.Id, Mon, 20, start: "10:00", mutate: b => { b.PitchId = pitchId; b.PitchSize = "8"; b.Sport = "football"; });
        await Book(venue.Id, Mon, 20, start: "12:00", mutate: b => { b.PitchId = pitchId; b.PitchSize = "8"; b.Sport = "football"; });

        var r = await Get<OccupancyReport>(_fx.CreateClientFor(owner.Id, "venue_owner"), "/api/v1/reports/occupancy" + Q(Mon, Mon));

        Assert.Equal(100, r.Grid.Single(c => c.Day == 1 && c.Hour == 10).Pct);
        Assert.Equal(50, r.Grid.Single(c => c.Day == 1 && c.Hour == 12).Pct);
        Assert.Null(r.Grid.Single(c => c.Day == 1 && c.Hour == 5).Pct); // closed
        Assert.Equal((15.0, 1.5), (r.OpenHours, r.BookedHours));
        Assert.Equal(10, r.Occupancy.Value);
        Assert.Equal(10, Assert.Single(r.ByPitch).Pct);
        Assert.Equal((1, 10), (r.Busiest[0].Day, r.Busiest[0].Hour));
    }

    [Fact]
    public async Task Occupancy_CountsAStandingWeekOnce_WhetherOrNotItWasRecorded()
    {
        var co = await NewCompany();
        var rule = await _fx.Insert(new PermanentBooking
        {
            VenueId = co.A.Id, DayOfWeek = 1, StartTime = "18:00", Duration = 60,
            Label = "Monday league", CreatedByUserId = co.Owner.Id, CreatedAt = new DateTime(2025, 12, 1),
        });
        // Recorded on the first Monday, not on the second: one hour each, never two.
        await Book(co.A.Id, Mon, 20, start: "18:00", mutate: b => b.PermanentBookingId = rule.Id);

        var r = await Get<OccupancyReport>(co.Client, $"/api/v1/reports/occupancy{Q(Mon, Mon.AddDays(7))}&venue_id={co.A.Id}");

        Assert.Equal(2, r.BookedHours);
        Assert.Equal(8 * 15, r.OpenHours);
    }

    // ── Customers & team ─────────────────────────────────────────────────────

    [Fact]
    public async Task Customers_SplitNewFromReturning_AndTheTeamShowsWhoTookTheMoney()
    {
        var co = await NewCompany();
        Customer NewCustomer(string name) => new()
        {
            OwnerId = co.Owner.Id, Name = name, Phone = "+96279" + Random.Shared.Next(1000000, 9999999),
        };
        var regular = await _fx.Insert(NewCustomer("Regular"));
        var newcomer = await _fx.Insert(NewCustomer("Newcomer"));
        await Book(co.A.Id, Mon.AddDays(-20), 20, status: "completed", mutate: b => b.CustomerId = regular.Id);
        var rb = await Book(co.A.Id, Mon, 20, status: "completed", mutate: b => b.CustomerId = regular.Id);
        var nb = await Book(co.A.Id, Mon, 30, status: "completed", mutate: b => b.CustomerId = newcomer.Id);
        var clerk = await _fx.CreateStaff(co.Owner.Id, "write");
        await Pay(rb, 20, Mon.AddHours(9), method: "cash", recordedBy: co.Owner.Id, customerId: regular.Id);
        await Pay(nb, 30, Mon.AddHours(9), method: "cliq", recordedBy: clerk.Id, customerId: newcomer.Id);

        var r = await Get<CustomersReport>(co.Client, "/api/v1/reports/customers" + Q(Mon, Mon));

        var c = r.Customers!;
        Assert.Equal((2.0, 1.0, 1), (c.Active.Value, c.New.Value, c.Returning));
        Assert.Equal(50, c.ReturnRate);
        Assert.Equal("Newcomer", c.TopBySpend[0].Name);
        Assert.Equal(1, c.TopByVisits.Single(t => t.Name == "Regular").Visits);

        var team = r.Team!;
        Assert.Equal((20.0, 20.0), (team.Single(m => m.UserId == co.Owner.Id).Collected, team.Single(m => m.UserId == co.Owner.Id).Cash));
        Assert.Equal((30.0, 30.0), (team.Single(m => m.UserId == clerk.Id).Collected, team.Single(m => m.UserId == clerk.Id).Cliq));
    }

    // ── Who sees what ────────────────────────────────────────────────────────

    private async Task<HttpClient> ClerkWith(Co co, string[] permissions, string[]? venueIds)
    {
        var role = await co.Client.PostAsJsonAsync("/api/v1/staff-roles",
            new { name = "Role " + Guid.NewGuid().ToString("N")[..6], permissions });
        var roleId = (await role.Content.ReadFromJsonAsync<ApiResponse<StaffRoleResponse>>())!.Data!.Id;
        var hire = await co.Client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Clerk", email = $"clerk-{Guid.NewGuid():N}@test.local", password = DatabaseFixture.TestPassword,
            role = "venue_staff", staffRoleId = roleId, allVenues = venueIds == null, venueIds,
        });
        Assert.Equal(HttpStatusCode.OK, hire.StatusCode);
        var clerk = (await hire.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;
        return await _fx.CreateClientForUserAsync(clerk.Id);
    }

    [Fact]
    public async Task AClerkLimitedToOneBranch_SeesOnlyThatBranch_AndNoNamesOrTeam()
    {
        var co = await NewCompany();
        var a = await Book(co.A.Id, Mon, 20, paid: 20);
        await Pay(a, 20, Mon.AddHours(9));
        var b = await Book(co.B.Id, Mon, 70, paid: 70);
        await Pay(b, 70, Mon.AddHours(9));
        var clerk = await ClerkWith(co, [StaffPermissions.ReportsView], [co.A.Id]);

        var money = await Get<MoneyReport>(clerk, "/api/v1/reports/money" + Q(Mon, Mon));
        Assert.Equal(20, money.Collected.Value);
        Assert.Equal(co.A.Id, Assert.Single(money.ByVenue).VenueId);

        // Asking for the other branch narrows to nothing rather than widening.
        var other = await Get<MoneyReport>(clerk, $"/api/v1/reports/money{Q(Mon, Mon)}&venue_id={co.B.Id}");
        Assert.Equal(0, other.Collected.Value);

        var customers = await Get<CustomersReport>(clerk, "/api/v1/reports/customers" + Q(Mon, Mon));
        Assert.Null(customers.Customers);
        Assert.Null(customers.Team);

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/v1/reports/platform")).StatusCode);
    }

    [Fact]
    public async Task AClerkWithCustomersView_SeesCustomersButStillNotTheTeam()
    {
        var co = await NewCompany();
        var clerk = await ClerkWith(co, [StaffPermissions.ReportsView, StaffPermissions.CustomersView], null);

        var r = await Get<CustomersReport>(clerk, "/api/v1/reports/customers" + Q(Mon, Mon));

        Assert.NotNull(r.Customers);
        Assert.Null(r.Team);
    }

    [Fact]
    public async Task ThePlatformReport_IsForAdminsOnly_AndListsEveryCompany()
    {
        var co = await NewCompany();
        await Book(co.A.Id, Mon, 45, status: "completed", manual: false, mutate: b => b.SystemFee = 2.25);

        Assert.Equal(HttpStatusCode.Forbidden, (await co.Client.GetAsync("/api/v1/reports/platform")).StatusCode);

        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var r = await Get<PlatformReport>(admin, "/api/v1/reports/platform" + Q(Mon, Mon));
        var row = r.Companies.Single(c => c.OwnerId == co.Owner.Id);
        Assert.Equal((2, 1, 45.0, 2.25), (row.Venues, row.Bookings, row.Booked, row.Fee));
        Assert.True(r.Booked.Value >= 45);
    }

    [Fact]
    public async Task ABadPeriod_IsRefusedWithAReason()
    {
        var co = await NewCompany();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await co.Client.GetAsync("/api/v1/reports/money?from=2026-02-01&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await co.Client.GetAsync("/api/v1/reports/money?from=2024-01-01&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await co.Client.GetAsync("/api/v1/reports/money?from=yesterday")).StatusCode);
    }
}
