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
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;

    public ReportsController(AppDbContext db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    private const int SparklineDays = 14;

    /// <summary>
    /// Which venues this request is allowed to aggregate over. <c>VenueIds == null</c>
    /// means platform-wide (super_admin only).
    /// </summary>
    private sealed record ReportScope(bool Allowed, List<string>? VenueIds)
    {
        public bool PlatformWide => VenueIds == null;
    }

    /// <summary>
    /// Resolves the caller's reporting scope. Deny-by-default: any role that is not
    /// explicitly handled gets nothing.
    ///
    /// Previously every endpoint here either had no scoping at all or used the pattern
    /// <c>if (UserRole == "venue_owner") ownerId = UserId;</c> followed by an
    /// <c>if (!string.IsNullOrEmpty(ownerId))</c> filter — which pinned exactly one role
    /// and let every other role (player, venue_staff, anything unrecognised) fall through
    /// to the unfiltered platform-wide branch. On shared infrastructure sold to competing
    /// venue owners that is a cross-tenant disclosure, so scope is now derived from the
    /// token and the client-supplied owner_id is honoured only for super_admin.
    /// </summary>
    private async Task<ReportScope> ResolveScopeAsync(string? requestedOwnerId)
    {
        if (UserRole == "super_admin")
        {
            if (string.IsNullOrEmpty(requestedOwnerId))
                return new ReportScope(true, null);

            var scopedIds = await _db.Venues
                .Where(v => v.OwnerId == requestedOwnerId)
                .Select(v => v.Id)
                .ToListAsync();
            return new ReportScope(true, scopedIds);
        }

        if (UserRole == "venue_owner")
        {
            // requestedOwnerId is deliberately ignored — an owner may only ever see
            // their own venues, whatever the query string asks for.
            var ownIds = await _db.Venues
                .Where(v => v.OwnerId == UserId)
                .Select(v => v.Id)
                .ToListAsync();
            return new ReportScope(true, ownIds);
        }

        return new ReportScope(false, null);
    }

    /// <summary>
    /// Compute per-day totals for the last <paramref name="days"/> days (oldest → newest),
    /// filling in zeros for days with no bookings. <paramref name="venueIds"/> null → all venues.
    /// </summary>
    private async Task<SummarySparklines> ComputeSparklinesAsync(List<string>? venueIds, int days, bool includeSystemRevenue)
    {
        var since = DateTime.UtcNow.Date.AddDays(-(days - 1));

        var query = _db.Bookings.Where(b => b.Date >= since);
        if (venueIds != null)
            query = query.Where(b => venueIds.Contains(b.VenueId));

        // Split into two passes: one for counts (all statuses) and one for revenue (completed only).
        var daily = await query
            .GroupBy(b => b.Date.Date)
            .Select(g => new
            {
                Day = g.Key,
                Bookings = g.Count(),
                Revenue = g.Where(x => x.Status == "completed").Sum(x => (double?)x.Amount) ?? 0,
                OwnerRevenue = g.Where(x => x.Status == "completed").Sum(x => (double?)x.OwnerAmount) ?? 0,
                SystemRevenue = g.Where(x => x.Status == "completed").Sum(x => (double?)x.SystemFee) ?? 0,
            })
            .ToListAsync();

        var byDay = daily.ToDictionary(d => d.Day);

        var revenue = new List<double>(days);
        var systemRevenue = new List<double>(days);
        var ownerRevenue = new List<double>(days);
        var bookings = new List<double>(days);

        for (var d = since; d <= DateTime.UtcNow.Date; d = d.AddDays(1))
        {
            if (byDay.TryGetValue(d, out var day))
            {
                revenue.Add(day.Revenue);
                systemRevenue.Add(includeSystemRevenue ? day.SystemRevenue : 0);
                ownerRevenue.Add(day.OwnerRevenue);
                bookings.Add(day.Bookings);
            }
            else
            {
                revenue.Add(0);
                systemRevenue.Add(0);
                ownerRevenue.Add(0);
                bookings.Add(0);
            }
        }

        return new SummarySparklines
        {
            Revenue = revenue,
            SystemRevenue = systemRevenue,
            OwnerRevenue = ownerRevenue,
            Bookings = bookings,
        };
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? owner_id = null)
    {
        var scope = await ResolveScopeAsync(owner_id);
        if (!scope.Allowed) return Forbid();

        if (!scope.PlatformWide)
        {
            // Owner-scoped summary — owner sees their 95% cut as "revenue"
            var venueIds = scope.VenueIds!;

            var totalVenues = venueIds.Count;
            var totalBookings = await _db.Bookings.CountAsync(b => venueIds.Contains(b.VenueId));
            var completedBookings = _db.Bookings
                .Where(b => venueIds.Contains(b.VenueId) && b.Status == "completed");
            var ownerRevenue = await completedBookings.SumAsync(b => b.OwnerAmount);
            var sparklines = await ComputeSparklinesAsync(venueIds, SparklineDays, includeSystemRevenue: false);

            return Ok(new ApiResponse<SummaryResponse>
            {
                Data = new SummaryResponse
                {
                    // What the owner actually keeps. Manual bookings carry no fee at all,
                    // so for a venue selling its own slots this is simply their takings.
                    TotalRevenue = ownerRevenue,
                    OwnerRevenue = ownerRevenue,
                    // The platform's own cut and rate are not the owner's business and are
                    // not sent to them — the product is their management system, not a
                    // marketplace they are a tenant of.
                    SystemRevenue = 0,
                    PlatformFeePercentage = 0,
                    TotalBookings = totalBookings,
                    TotalVenues = totalVenues,
                    TotalUsers = 0,
                    Sparklines = sparklines,
                }
            });
        }

        // Admin summary — sees gross, system cut, and owner payouts
        var completed = _db.Bookings.Where(b => b.Status == "completed");
        var grossRevenue = await completed.SumAsync(b => b.Amount);
        var systemRevenue = await completed.SumAsync(b => b.SystemFee);
        var totalOwnerRevenue = await completed.SumAsync(b => b.OwnerAmount);
        var adminSparklines = await ComputeSparklinesAsync(venueIds: null, SparklineDays, includeSystemRevenue: true);

        return Ok(new ApiResponse<SummaryResponse>
        {
            Data = new SummaryResponse
            {
                TotalRevenue = grossRevenue,
                OwnerRevenue = totalOwnerRevenue,
                SystemRevenue = systemRevenue,
                // Read from settings, not the constant. Bookings have always charged the
                // configured rate while this reported a hardcoded 5.0 — so after an admin
                // changed the fee, the dashboard quietly stated the wrong number.
                PlatformFeePercentage = await _settings.GetPlatformFeePercentageAsync(),
                TotalBookings = await _db.Bookings.CountAsync(),
                TotalVenues = await _db.Venues.CountAsync(),
                TotalUsers = await _db.Users.CountAsync(),
                Sparklines = adminSparklines,
            }
        });
    }

    [HttpGet("revenue-chart")]
    public async Task<IActionResult> RevenueChart([FromQuery] int days = 30, [FromQuery] string? owner_id = null)
    {
        var scope = await ResolveScopeAsync(owner_id);
        if (!scope.Allowed) return Forbid();

        var since = DateTime.UtcNow.Date.AddDays(-days);

        var query = _db.Bookings.Where(b => b.Status == "completed" && b.Date >= since);
        if (!scope.PlatformWide)
            query = query.Where(b => scope.VenueIds!.Contains(b.VenueId));

        var bookings = await query.ToListAsync();

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

    // Ranked by revenue. Unscoped this was a named competitor leaderboard: any owner
    // could read every rival venue's completed revenue, and the dashboard rendered it
    // on the owner's own home screen.
    [HttpGet("top-venues")]
    public async Task<IActionResult> TopVenues([FromQuery] string? owner_id = null)
    {
        var scope = await ResolveScopeAsync(owner_id);
        if (!scope.Allowed) return Forbid();

        var baseQuery = _db.Bookings.Where(b => b.Status == "completed");
        if (!scope.PlatformWide)
            baseQuery = baseQuery.Where(b => scope.VenueIds!.Contains(b.VenueId));

        var data = await baseQuery
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
    public async Task<IActionResult> SportsBreakdown([FromQuery] string? owner_id = null)
    {
        var scope = await ResolveScopeAsync(owner_id);
        if (!scope.Allowed) return Forbid();

        var baseQuery = _db.Bookings.Where(b => b.Sport != null);
        if (!scope.PlatformWide)
            baseQuery = baseQuery.Where(b => scope.VenueIds!.Contains(b.VenueId));

        var data = await baseQuery
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

    // Unscoped, this returned every booking on the platform — venue names, player names
    // and amounts — to any authenticated caller, and the dashboard shows the Export
    // button to venue owners. It was a one-click cross-tenant customer-list dump.
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string format = "csv",
        [FromQuery(Name = "from")] string? fromDate = null,
        [FromQuery(Name = "to")] string? toDate = null,
        [FromQuery] string? venue_id = null,
        [FromQuery] string? owner_id = null)
    {
        var scope = await ResolveScopeAsync(owner_id);
        if (!scope.Allowed) return Forbid();

        var query = _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsQueryable();

        if (!scope.PlatformWide)
            query = query.Where(b => scope.VenueIds!.Contains(b.VenueId));

        // A venue_id filter narrows within the caller's scope; it can never widen it,
        // so asking for a competitor's venue yields nothing rather than their data.
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
                sb.AppendLine(string.Join(',',
                    Csv(b.Id),
                    Csv(b.Venue.Name),
                    Csv(b.Player?.Name),
                    Csv(b.Sport),
                    Csv(b.Date.ToString("yyyy-MM-ddTHH:mm:ssZ")),
                    Csv(b.Duration.ToString(CultureInfo.InvariantCulture)),
                    Csv(b.Amount.ToString(CultureInfo.InvariantCulture)),
                    Csv(b.Status)));
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "report.csv");
        }

        // PDF placeholder — return a simple text file since we don't have a PDF library
        var pdfContent = $"Report generated at {DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\n\nBookings: {bookings.Count}";
        return File(Encoding.UTF8.GetBytes(pdfContent), "application/pdf", "report.pdf");
    }

    /// <summary>
    /// Quote a value for CSV. Fields were previously interpolated raw, so a venue or
    /// customer name containing a comma or quote silently corrupted the row, and a name
    /// beginning with = + - or @ was evaluated as a formula when the file was opened in
    /// Excel. Both are fixed here: always quote, double embedded quotes, and neutralise
    /// leading formula characters with a apostrophe.
    /// </summary>
    private static string Csv(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
