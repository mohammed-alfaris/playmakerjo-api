using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Customers;
using SportsVenueApi.Helpers;

namespace SportsVenueApi.Controllers;

/// <summary>
/// The venue's own customer book.
///
/// There is deliberately NO create endpoint. A customer exists because a booking was
/// recorded — the list builds itself as a by-product of work the owner is already doing.
/// An "Add customer" button would be a screen nobody ever fills in.
/// </summary>
[ApiController]
[Route("api/v1/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly AppDbContext _db;

    public CustomersController(AppDbContext db) => _db = db;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";
    private string? StaffOwnerId => User.FindFirstValue("owner_id");

    /// <summary>
    /// Whose book is this? Owners get their own, staff get their employer's, and everyone
    /// else — players, unlinked staff, unknown roles — gets nothing. Admins must name an
    /// owner explicitly: there is no cross-owner customer list at all, because customers
    /// are the single most sensitive thing a competitor could take.
    /// </summary>
    private string? ResolveOwnerId(string? requestedOwnerId) => UserRole switch
    {
        "super_admin" => string.IsNullOrWhiteSpace(requestedOwnerId) ? null : requestedOwnerId,
        "venue_owner" => UserId,
        "venue_staff" => string.IsNullOrEmpty(StaffOwnerId) ? null : StaffOwnerId,
        _ => null,
    };

    /// <summary>A booking reduced to just what the stats need.</summary>
    private sealed record BookingFact(
        string CustomerId, DateTime Date, string Status,
        double TotalAmount, double AmountPaid, bool IsManual);

    /// <summary>
    /// Computes stats for a set of customers in one pass. Called with a single page of
    /// customers (20), so this is one query over an index, not an N+1.
    /// </summary>
    private async Task<Dictionary<string, CustomerStats>> ComputeStatsAsync(List<string> customerIds)
    {
        var result = customerIds.ToDictionary(id => id, _ => new CustomerStats());
        if (customerIds.Count == 0) return result;

        var facts = await _db.Bookings
            .Where(b => b.CustomerId != null && customerIds.Contains(b.CustomerId))
            .Select(b => new BookingFact(
                b.CustomerId!, b.Date, b.Status, b.TotalAmount, b.AmountPaid, b.IsManual))
            .ToListAsync();

        var today = PlatformConstants.JordanToday();
        var ninetyDaysAgo = today.AddDays(-90);

        foreach (var group in facts.GroupBy(f => f.CustomerId))
        {
            var s = result[group.Key];
            var rows = group.ToList();

            var live = rows.Where(r => r.Status != "cancelled").ToList();

            s.TotalBookings = live.Count;
            s.Cancelled = rows.Count(r => r.Status == "cancelled");
            s.NoShow = rows.Count(r => r.Status == "no_show");

            // Presence assumed, absence declared — see CustomerStats.Attended.
            s.Attended = rows.Count(r =>
                r.Status == "completed" || (r.Status == "confirmed" && r.Date.Date < today));

            s.Upcoming = rows.Count(r => r.Status == "confirmed" && r.Date.Date >= today);

            var owing = live.Where(r => r.AmountPaid + 0.001 < r.TotalAmount).ToList();
            s.Unpaid = owing.Count;
            s.AmountOwed = Math.Round(owing.Sum(r => r.TotalAmount - r.AmountPaid), 3);

            s.ViaCounter = live.Count(r => r.IsManual);
            s.ViaApp = live.Count(r => !r.IsManual);

            var past = live.Where(r => r.Date.Date < today).ToList();
            if (past.Count > 0)
            {
                var last = past.Max(r => r.Date.Date);
                s.LastVisit = last.ToString("yyyy-MM-dd");
                s.DaysSinceLastVisit = (int)(today - last).TotalDays;
            }

            if (rows.Count > 0)
                s.CustomerSince = rows.Min(r => r.Date.Date).ToString("yyyy-MM-dd");

            var judged = s.Attended + s.NoShow;
            s.NoShowRate = judged == 0 ? 0 : Math.Round(s.NoShow * 100.0 / judged, 1);

            s.IsRegular = live.Count(r => r.Date.Date >= ninetyDaysAgo) >= 4;
            s.IsLapsed = s.TotalBookings >= 2 && s.DaysSinceLastVisit is > 30;
            s.IsNew = s.TotalBookings <= 1;
            s.IsUnreliable = s.NoShow >= 2 || s.NoShowRate > 25;
        }

        return result;
    }

    /// <summary>
    /// GET /api/v1/customers/lookup?phone=… — the moment a phone number is typed at the
    /// counter.
    ///
    /// Returns 200 with <c>data: null</c> for an unknown number rather than 404: "I don't
    /// know them" is a normal, expected answer, and making the client handle an error
    /// response for it would put a red state in front of the owner every time a new
    /// customer walks in.
    /// </summary>
    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string? phone, [FromQuery] string? owner_id = null)
    {
        var ownerId = ResolveOwnerId(owner_id);
        if (ownerId == null) return Forbid();

        var canonical = PhoneNormalizer.ToE164Jo(phone);
        if (canonical == null)
            return Ok(new ApiResponse<CustomerLookupResponse?> { Data = null, Message = "Not a Jordanian mobile" });

        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.Phone == canonical);

        if (customer == null)
            return Ok(new ApiResponse<CustomerLookupResponse?> { Data = null, Message = "New customer" });

        var stats = await ComputeStatsAsync([customer.Id]);

        return Ok(new ApiResponse<CustomerLookupResponse?>
        {
            Data = new CustomerLookupResponse
            {
                Id = customer.Id,
                Name = customer.Name,
                Phone = customer.Phone,
                Note = customer.Note,
                Stats = stats[customer.Id],
            }
        });
    }

    /// <summary>
    /// GET /api/v1/customers — the book, with search and the three segments the screen
    /// offers: everyone, regulars, and the ones who stopped coming.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? segment = null,
        [FromQuery] string? owner_id = null)
    {
        var ownerId = ResolveOwnerId(owner_id);
        if (ownerId == null) return Forbid();
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Customers.Where(c => c.OwnerId == ownerId && c.Status == "active");

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Search by name OR phone. The typed phone is normalised first so "079…" finds
            // a row stored as "+96279…".
            var canonical = PhoneNormalizer.ToE164Jo(search);
            var term = search.Trim();
            query = canonical != null
                ? query.Where(c => c.Phone == canonical || EF.Functions.Like(c.Name, $"%{term}%"))
                : query.Where(c => EF.Functions.Like(c.Name, $"%{term}%") || EF.Functions.Like(c.Phone, $"%{term}%"));
        }

        // Segment filtering needs the derived stats, which are not in the table. At this
        // scale (hundreds of customers per owner) it is honest and cheap to page after
        // filtering in memory rather than denormalising counters that would drift.
        var all = await query.OrderByDescending(c => c.CreatedAt).ToListAsync();
        var stats = await ComputeStatsAsync(all.Select(c => c.Id).ToList());

        IEnumerable<Models.Customer> filtered = all;
        if (segment == "regulars") filtered = all.Where(c => stats[c.Id].IsRegular);
        else if (segment == "lapsed") filtered = all.Where(c => stats[c.Id].IsLapsed);
        else if (segment == "unreliable") filtered = all.Where(c => stats[c.Id].IsUnreliable);
        else if (segment == "owing") filtered = all.Where(c => stats[c.Id].Unpaid > 0);

        var list = filtered.ToList();
        var total = list.Count;
        var pageItems = list.Skip((page - 1) * limit).Take(limit).ToList();

        return Ok(new ApiResponse<List<CustomerResponse>>
        {
            Data = pageItems.Select(c => ToDto(c, stats[c.Id])).ToList(),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    /// <summary>
    /// GET /api/v1/customers/report?month=YYYY-MM — the month in one screen.
    ///
    /// Registered before the {id} route so "report" is not read as a customer id.
    /// </summary>
    [HttpGet("report")]
    public async Task<IActionResult> Report([FromQuery] string? month = null, [FromQuery] string? owner_id = null)
    {
        var ownerId = ResolveOwnerId(owner_id);
        if (ownerId == null) return Forbid();

        var today = PlatformConstants.JordanToday();
        var start = ParseMonth(month) ?? new DateTime(today.Year, today.Month, 1);
        var end = start.AddMonths(1);

        var customers = await _db.Customers
            .Where(c => c.OwnerId == ownerId)
            .Select(c => new { c.Id, c.Name, c.Phone, c.Status })
            .ToListAsync();

        var ids = customers.Select(c => c.Id).ToList();

        // An owner with no customers yet still gets the full shape — six months of zeros
        // rather than an empty object, so the trend renders an axis instead of nothing.
        var facts = ids.Count == 0
            ? []
            : await _db.Bookings
                .Where(b => b.CustomerId != null && ids.Contains(b.CustomerId) && b.Status != "cancelled")
                .Select(b => new { CustomerId = b.CustomerId!, b.Date, b.Status })
                .ToListAsync();

        // "Came" uses the same rule as everywhere else: explicitly completed, or a past
        // booking nobody flagged as a no-show. See CustomerStats.Attended.
        bool Attended(DateTime date, string status) =>
            status == "completed" || (status == "confirmed" && date.Date < today);

        var byCustomer = facts.GroupBy(f => f.CustomerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var report = new CustomerReportResponse
        {
            Month = start.ToString("yyyy-MM"),
            TotalCustomers = customers.Count(c => c.Status == "active"),
        };

        var tops = new List<TopCustomerItem>();
        var lapsed = new List<TopCustomerItem>();

        foreach (var c in customers)
        {
            if (!byCustomer.TryGetValue(c.Id, out var rows)) continue;

            var inMonth = rows.Where(r => r.Date.Date >= start && r.Date.Date < end).ToList();
            var visitsThisMonth = inMonth.Count(r => Attended(r.Date, r.Status));
            var noShowsThisMonth = inMonth.Count(r => r.Status == "no_show");

            if (inMonth.Count > 0)
            {
                report.Active++;
                report.Visits += visitsThisMonth;
                report.NoShows += noShowsThisMonth;

                // First booking ever inside this month => a genuinely new face.
                var firstEver = rows.Min(r => r.Date.Date);
                if (firstEver >= start && firstEver < end) report.NewCustomers++;
                else report.Returning++;
            }

            var lifetimeVisits = rows.Count(r => Attended(r.Date, r.Status));
            var past = rows.Where(r => r.Date.Date < today).ToList();
            DateTime? last = past.Count > 0 ? past.Max(r => r.Date.Date) : null;
            var daysSince = last == null ? (int?)null : (int)(today - last.Value).TotalDays;

            var item = new TopCustomerItem
            {
                Id = c.Id,
                Name = c.Name,
                Phone = c.Phone,
                Visits = visitsThisMonth,
                NoShow = noShowsThisMonth,
                LastVisit = last?.ToString("yyyy-MM-dd"),
                DaysSinceLastVisit = daysSince,
                LifetimeVisits = lifetimeVisits,
            };

            if (visitsThisMonth > 0) tops.Add(item);

            // The win-back list. Same rule as the "stopped coming" segment: came at least
            // twice, then went quiet for over a month. A one-off visitor is not a lost
            // regular and chasing them wastes the owner's time.
            if (c.Status == "active" && lifetimeVisits >= 2 && daysSince is > 30)
                lapsed.Add(item);
        }

        report.Returning = report.Active - report.NewCustomers;
        report.ReturnRate = report.Active == 0
            ? 0
            : Math.Round(report.Returning * 100.0 / report.Active, 1);
        report.LapsedCount = lapsed.Count;

        report.TopCustomers = tops
            .OrderByDescending(x => x.Visits)
            .ThenByDescending(x => x.LifetimeVisits)
            .Take(10)
            .ToList();

        // Most recently lost first — they are the easiest to win back.
        report.Lapsed = lapsed
            .OrderBy(x => x.DaysSinceLastVisit)
            .Take(10)
            .ToList();

        // Six months of new-vs-returning, so a leaking bucket is visible as a shape rather
        // than a single number.
        for (var i = 5; i >= 0; i--)
        {
            var mStart = start.AddMonths(-i);
            var mEnd = mStart.AddMonths(1);
            var point = new CustomerMonthPoint { Month = mStart.ToString("yyyy-MM") };

            foreach (var rows in byCustomer.Values)
            {
                var inM = rows.Where(r => r.Date.Date >= mStart && r.Date.Date < mEnd).ToList();
                if (inM.Count == 0) continue;
                var firstEver = rows.Min(r => r.Date.Date);
                if (firstEver >= mStart && firstEver < mEnd) point.NewCustomers++;
                else point.ReturningCustomers++;
            }

            report.Trend.Add(point);
        }

        return Ok(new ApiResponse<CustomerReportResponse> { Data = report });
    }

    /// <summary>Parses "YYYY-MM" into the first of that month. Null when absent or malformed.</summary>
    private static DateTime? ParseMonth(string? month)
    {
        if (string.IsNullOrWhiteSpace(month)) return null;
        return DateTime.TryParseExact(
            month + "-01", "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>GET /api/v1/customers/{id} — the full picture plus their recent bookings.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, [FromQuery] int recent = 10)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Customer not found" });

        var ownerId = ResolveOwnerId(customer.OwnerId);
        if (ownerId == null || ownerId != customer.OwnerId) return Forbid();

        if (recent is < 1 or > 50) recent = 10;

        var stats = await ComputeStatsAsync([customer.Id]);

        var bookings = await _db.Bookings
            .Include(b => b.Venue)
            .Where(b => b.CustomerId == customer.Id)
            .OrderByDescending(b => b.Date)
            .Take(recent)
            .Select(b => new CustomerBookingItem
            {
                Id = b.Id,
                VenueName = b.Venue.Name,
                Sport = b.Sport,
                Date = b.Date.ToString("yyyy-MM-dd"),
                StartTime = b.StartTime,
                Status = b.Status,
                TotalAmount = b.TotalAmount,
                AmountPaid = b.AmountPaid,
                IsManual = b.IsManual,
            })
            .ToListAsync();

        var dto = ToDto(customer, stats[customer.Id]);
        return Ok(new ApiResponse<CustomerDetailResponse>
        {
            Data = new CustomerDetailResponse
            {
                Id = dto.Id,
                Name = dto.Name,
                Phone = dto.Phone,
                Note = dto.Note,
                Status = dto.Status,
                CreatedAt = dto.CreatedAt,
                Stats = dto.Stats,
                RecentBookings = bookings,
            }
        });
    }

    /// <summary>PATCH /api/v1/customers/{id} — fix a typo'd name, or add the margin note.</summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateCustomerRequest req)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Customer not found" });

        var ownerId = ResolveOwnerId(customer.OwnerId);
        if (ownerId == null || ownerId != customer.OwnerId) return Forbid();

        if (req.Name != null)
        {
            var name = req.Name.Trim();
            if (name.Length == 0)
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Name cannot be empty" });
            customer.Name = name.Length > 120 ? name[..120] : name;
        }

        if (req.Note != null)
        {
            var note = req.Note.Trim();
            customer.Note = note.Length == 0 ? null : note.Length > 280 ? note[..280] : note;
        }

        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var stats = await ComputeStatsAsync([customer.Id]);
        return Ok(new ApiResponse<CustomerResponse> { Data = ToDto(customer, stats[customer.Id]), Message = "Saved" });
    }

    /// <summary>
    /// PATCH /api/v1/customers/{id}/archive — hide a junk row from a mistyped number.
    /// Archive, never delete: bookings reference this record and they are financial history.
    /// </summary>
    [HttpPatch("{id}/archive")]
    public async Task<IActionResult> Archive(string id)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Customer not found" });

        var ownerId = ResolveOwnerId(customer.OwnerId);
        if (ownerId == null || ownerId != customer.OwnerId) return Forbid();

        customer.Status = "archived";
        customer.ArchivedAt = DateTime.UtcNow;
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var stats = await ComputeStatsAsync([customer.Id]);
        return Ok(new ApiResponse<CustomerResponse> { Data = ToDto(customer, stats[customer.Id]), Message = "Archived" });
    }

    private static CustomerResponse ToDto(Models.Customer c, CustomerStats stats) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Phone = c.Phone,
        Note = c.Note,
        Status = c.Status,
        CreatedAt = c.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        Stats = stats,
    };
}
