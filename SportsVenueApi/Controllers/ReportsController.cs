using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Reports;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db) => _db = db;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? owner_id = null)
    {
        var ownerId = owner_id;
        if (UserRole == "venue_owner")
            ownerId = UserId;

        if (!string.IsNullOrEmpty(ownerId))
        {
            // Owner-scoped summary — owner sees their 95% cut as "revenue"
            var venueIds = await _db.Venues
                .Where(v => v.OwnerId == ownerId)
                .Select(v => v.Id)
                .ToListAsync();

            var totalVenues = venueIds.Count;
            var totalBookings = await _db.Bookings.CountAsync(b => venueIds.Contains(b.VenueId));
            var completedBookings = _db.Bookings
                .Where(b => venueIds.Contains(b.VenueId) && b.Status == "completed");
            var ownerRevenue = await completedBookings.SumAsync(b => b.OwnerAmount);

            return Ok(new ApiResponse<SummaryResponse>
            {
                Data = new SummaryResponse
                {
                    TotalRevenue = ownerRevenue,  // Owner sees their cut as total revenue
                    OwnerRevenue = ownerRevenue,
                    SystemRevenue = 0,            // Hidden from owner
                    PlatformFeePercentage = PlatformConstants.SystemFeePercentage,
                    TotalBookings = totalBookings,
                    TotalVenues = totalVenues,
                    TotalUsers = 0,
                    RevenueChange = 12.4,
                    BookingsChange = 8.1,
                    VenuesChange = 14.3,
                    UsersChange = 5.0
                }
            });
        }

        // Admin summary — sees gross, system cut, and owner payouts
        var completed = _db.Bookings.Where(b => b.Status == "completed");
        var grossRevenue = await completed.SumAsync(b => b.Amount);
        var systemRevenue = await completed.SumAsync(b => b.SystemFee);
        var totalOwnerRevenue = await completed.SumAsync(b => b.OwnerAmount);

        return Ok(new ApiResponse<SummaryResponse>
        {
            Data = new SummaryResponse
            {
                TotalRevenue = grossRevenue,
                OwnerRevenue = totalOwnerRevenue,
                SystemRevenue = systemRevenue,
                PlatformFeePercentage = PlatformConstants.SystemFeePercentage,
                TotalBookings = await _db.Bookings.CountAsync(),
                TotalVenues = await _db.Venues.CountAsync(),
                TotalUsers = await _db.Users.CountAsync(),
                RevenueChange = 12.4,
                BookingsChange = 8.1,
                VenuesChange = 14.3,
                UsersChange = 5.0
            }
        });
    }

    [HttpGet("revenue-chart")]
    public async Task<IActionResult> RevenueChart([FromQuery] int days = 30)
    {
        var since = DateTime.UtcNow.Date.AddDays(-days);

        var bookings = await _db.Bookings
            .Where(b => b.Status == "completed" && b.Date >= since)
            .ToListAsync();

        var grouped = bookings
            .GroupBy(b => b.Date.Date)
            .ToDictionary(g => g.Key, g => new
            {
                Revenue = g.Sum(b => b.Amount),
                OwnerRevenue = g.Sum(b => b.OwnerAmount),
                SystemRevenue = g.Sum(b => b.SystemFee)
            });

        var result = new List<RevenueChartPoint>();
        for (var d = since; d <= DateTime.UtcNow.Date; d = d.AddDays(1))
        {
            var day = grouped.GetValueOrDefault(d);
            result.Add(new RevenueChartPoint
            {
                Date = d.ToString("yyyy-MM-dd"),
                Revenue = day?.Revenue ?? 0,
                OwnerRevenue = day?.OwnerRevenue ?? 0,
                SystemRevenue = day?.SystemRevenue ?? 0
            });
        }

        return Ok(new ApiResponse<List<RevenueChartPoint>> { Data = result });
    }

    [HttpGet("top-venues")]
    public async Task<IActionResult> TopVenues()
    {
        var data = await _db.Bookings
            .Where(b => b.Status == "completed")
            .Include(b => b.Venue)
            .GroupBy(b => new { b.VenueId, b.Venue.Name })
            .Select(g => new TopVenueItem
            {
                Id = g.Key.VenueId,
                Name = g.Key.Name,
                Revenue = g.Sum(b => b.Amount),
                OwnerRevenue = g.Sum(b => b.OwnerAmount),
                SystemRevenue = g.Sum(b => b.SystemFee)
            })
            .OrderByDescending(x => x.Revenue)
            .Take(5)
            .ToListAsync();

        return Ok(new ApiResponse<List<TopVenueItem>> { Data = data });
    }

    [HttpGet("sports-breakdown")]
    public async Task<IActionResult> SportsBreakdown()
    {
        var data = await _db.Bookings
            .Where(b => b.Sport != null)
            .GroupBy(b => b.Sport!)
            .Select(g => new SportBreakdownItem
            {
                Sport = g.Key,
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .ToListAsync();

        // Capitalize sport names
        foreach (var item in data)
            item.Sport = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(item.Sport);

        return Ok(new ApiResponse<List<SportBreakdownItem>> { Data = data });
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string format = "csv",
        [FromQuery(Name = "from")] string? fromDate = null,
        [FromQuery(Name = "to")] string? toDate = null,
        [FromQuery] string? venue_id = null)
    {
        var query = _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsQueryable();

        if (!string.IsNullOrEmpty(venue_id))
            query = query.Where(b => b.VenueId == venue_id);

        if (!string.IsNullOrEmpty(fromDate) && DateTime.TryParse(fromDate, out var from))
            query = query.Where(b => b.Date >= from);

        if (!string.IsNullOrEmpty(toDate) && DateTime.TryParse(toDate, out var to))
            query = query.Where(b => b.Date <= to);

        var bookings = await query.OrderByDescending(b => b.Date).ToListAsync();

        if (format == "csv")
        {
            var sb = new StringBuilder();
            sb.AppendLine("ID,Venue,Player,Sport,Date,Duration,Amount,Status");
            foreach (var b in bookings)
            {
                sb.AppendLine($"{b.Id},{b.Venue.Name},{b.Player.Name},{b.Sport},{b.Date:yyyy-MM-ddTHH:mm:ssZ},{b.Duration},{b.Amount},{b.Status}");
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "report.csv");
        }

        // PDF placeholder — return a simple text file since we don't have a PDF library
        var pdfContent = $"Report generated at {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n\nBookings: {bookings.Count}";
        return File(Encoding.UTF8.GetBytes(pdfContent), "application/pdf", "report.pdf");
    }
}
