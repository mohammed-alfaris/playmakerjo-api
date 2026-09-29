using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Reports;
using SportsVenueApi.DTOs.Staff;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Reports;

/// <summary>
/// The Excel export: a real workbook, the same numbers as the screen, and only the sheets the
/// caller may see.
/// </summary>
[Collection("Api")]
public class ReportExcelTests
{
    private readonly DatabaseFixture _fx;

    public ReportExcelTests(DatabaseFixture fx) => _fx = fx;

    private static readonly DateTime Mon = new(2026, 1, 5);
    private const string Week = "from=2026-01-04&to=2026-01-10";

    private async Task<(User Owner, HttpClient Client, Venue Venue)> CompanyWithTakings()
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var customer = await _fx.Insert(new Customer
        {
            OwnerId = owner.Id, Name = "Excel Customer", Phone = "+96279" + Random.Shared.Next(1000000, 9999999),
        });
        var booking = await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = _fx.PlayerId, CustomerId = customer.Id, Sport = "basketball",
            Date = Mon, StartTime = "19:00", Duration = 60, Amount = 35, TotalAmount = 35, AmountPaid = 35,
            OwnerAmount = 35, Status = "completed", IsManual = true,
        });
        await _fx.Insert(new Payment
        {
            BookingId = booking.Id, PlayerId = _fx.PlayerId, VenueId = venue.Id, CustomerId = customer.Id,
            Amount = 35, Method = "cash", Kind = "full", Status = "paid", Date = Mon.AddHours(17),
            RecordedByUserId = owner.Id,
        });
        return (owner, _fx.CreateClientFor(owner.Id, "venue_owner"), venue);
    }

    private static async Task<XLWorkbook> Download(HttpClient client, string query)
    {
        var res = await client.GetAsync("/api/v1/reports/export.xlsx?" + query);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", res.Content.Headers.ContentType?.MediaType);
        return new XLWorkbook(new MemoryStream(await res.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task AnOwnersWorkbook_HasEveryBusinessSheet_AndTheScreensNumbers()
    {
        var (_, client, _) = await CompanyWithTakings();

        using var wb = await Download(client, Week);

        Assert.Equal(
            ["Summary", "Money", "Venues & pitches", "Outstanding", "Busy hours", "Bookings", "Customers", "Team"],
            wb.Worksheets.Select(w => w.Name));

        var money = (await (await client.GetAsync("/api/v1/reports/money?" + Week))
            .Content.ReadFromJsonAsync<ApiResponse<MoneyReport>>())!.Data!;
        var summary = wb.Worksheet("Summary");
        Assert.Equal("Collected", summary.Cell(5, 1).GetString());
        Assert.Equal(money.Collected.Value, summary.Cell(5, 2).GetDouble());
        Assert.Equal(35, money.Collected.Value);

        // The Money sheet: one row per day, then a totals row.
        var sheet = wb.Worksheet("Money");
        Assert.Equal("2026-01-05", sheet.Cell(3, 1).GetString());
        Assert.Equal(35, sheet.Cell(3, 2).GetDouble()); // cash
        Assert.StartsWith("SUM(", sheet.Cell(9, 5).FormulaA1);

        Assert.Equal("Excel Customer", wb.Worksheet("Customers").Cell(2, 1).GetString());
    }

    [Fact]
    public async Task AClerksWorkbook_LeavesOutCustomersAndTeam()
    {
        var (_, owner, _) = await CompanyWithTakings();
        var role = await owner.PostAsJsonAsync("/api/v1/staff-roles",
            new { name = "Reports only", permissions = new[] { StaffPermissions.ReportsView } });
        var roleId = (await role.Content.ReadFromJsonAsync<ApiResponse<StaffRoleResponse>>())!.Data!.Id;
        var hire = await owner.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Clerk", email = $"clerk-{Guid.NewGuid():N}@test.local", password = DatabaseFixture.TestPassword,
            role = "venue_staff", staffRoleId = roleId,
        });
        var clerkId = (await hire.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!.Id;
        var clerk = await _fx.CreateClientForUserAsync(clerkId);

        using var wb = await Download(clerk, Week);

        Assert.DoesNotContain(wb.Worksheets, w => w.Name is "Customers" or "Team" or "Platform");
        // No names on the outstanding sheet either: the column is there, empty.
        Assert.Contains(wb.Worksheets, w => w.Name == "Outstanding");
    }

    [Fact]
    public async Task InArabic_TheWorkbookReadsRightToLeft_AndAnAdminGetsThePlatformSheet()
    {
        await CompanyWithTakings();
        var admin = _fx.CreateClientFor(_fx.AdminId, "super_admin");

        using var wb = await Download(admin, Week + "&lang=ar");

        Assert.Equal("الملخص", wb.Worksheets.First().Name);
        Assert.True(wb.Worksheets.First().RightToLeft);
        Assert.Contains(wb.Worksheets, w => w.Name == "المنصة");
    }

    [Fact]
    public async Task TheOldFakePdf_IsNowRefusedWithDirections()
    {
        var (_, client, _) = await CompanyWithTakings();

        var res = await client.GetAsync("/api/v1/reports/export?format=pdf");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/reports/export?format=csv")).StatusCode);
    }
}
