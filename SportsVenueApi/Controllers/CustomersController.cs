using System.Security.Claims;
using System.Text;
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
    private string? StaffPermissions => User.FindFirstValue("permissions");

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

    /// <summary>
    /// May the caller change the book, as opposed to read it? <c>ResolveOwnerId</c>
    /// settles WHOSE book; this settles whether they may write to it.
    ///
    /// Every mutating booking route goes through <c>VenueAccess.CanWrite</c>, but that
    /// helper takes a Venue and customers are keyed on the owner, so nothing here
    /// consulted the permission at all — a clerk explicitly set to "read", whose own
    /// UI copy reads "Cannot create or change anything", could rename or archive every
    /// customer their employer had. Fails closed: a staff row with no permissions claim
    /// gets no write.
    /// </summary>
    private bool CanWriteCustomers =>
        UserRole != "venue_staff" || StaffPermissions == "write";

    /// <summary>A booking reduced to just what the stats need.</summary>
    private sealed record BookingFact(
        string CustomerId, DateTime Date, string Status,
        double TotalAmount, double AmountPaid, bool IsManual);

    /// <summary>
    /// Computes stats for a set of customers in one pass. Called with a single page of
    /// customers (20), so this is one query over an index, not an N+1.
    /// </summary>
    /// <summary>
    /// "Stopped coming": came at least twice, then went quiet for over a month.
    ///
    /// One rule, one place. It was written twice and the two copies disagreed — the segment
    /// list counted every non-cancelled booking, so a no-show or a booking still on the
    /// calendar made someone "lapsed", while the win-back report counted only visits
    /// actually attended. A customer with one visit 60 days ago and a booking next Tuesday
    /// appeared in the list as lost and not in the report, so the owner saw a count on one
    /// screen and a different-length list on the other.
    ///
    /// Attended visits is the correct input: a person who never turned up was not a regular,
    /// and a person with a booking on the calendar has not gone quiet.
    /// </summary>
    private static bool IsLapsedRule(int attendedVisits, int? daysSinceLastVisit) =>
        attendedVisits >= 2 && daysSinceLastVisit is > 30;

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
            s.Completed = rows.Count(r => r.Status == "completed");

            // Presence assumed, absence declared — see CustomerStats.Attended.
            bool Attended(BookingFact r) => r.Status == "completed" || (r.Status == "confirmed" && r.Date.Date < today);

            s.Attended = rows.Count(Attended);

            s.Upcoming = rows.Count(r => r.Status == "confirmed" && r.Date.Date >= today);

            // "Owed" means one specific thing: he played at the counter and still hasn't
            // settled up. Two other unpaid-looking cases must NOT count as owed:
            //   - Not yet attended (upcoming confirmed, or pending_payment) — nothing has
            //     been rendered yet, so there is nothing to have paid for. That state is
            //     already visible via Upcoming / the booking's own status.
            //   - No-show — he didn't come, so "still owes for the match" is meaningless;
            //     that is what NoShow is for, and conflating the two hid genuine no-shows
            //     inside a dinar figure that read as unpaid revenue.
            // App bookings are excluded outright: payment there is enforced by the
            // platform's own flow (deposit/CliQ proof) before or at booking time, not by
            // the owner deciding to let someone play on credit — he has no lever over it,
            // so it is not his receivable to chase.
            var owing = live.Where(r => r.IsManual && Attended(r) && r.AmountPaid + 0.001 < r.TotalAmount).ToList();
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
            s.IsLapsed = IsLapsedRule(s.Attended, s.DaysSinceLastVisit);
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
    /// GET /api/v1/customers/export — the owner's own customer list, as CSV.
    ///
    /// This is deliberately ungated and always will be. These are HIS customers: he
    /// collected every one of these numbers by taking bookings. In a market where venue
    /// owners have been burned by platforms that took their customer list hostage,
    /// a working export button is the cheapest credible proof that this one will not.
    ///
    /// Archived customers are included. Leaving someone out of an export because a flag
    /// was flipped in our product would make the file quietly incomplete, which is worse
    /// than not offering the export at all.
    ///
    /// The commercial argument for gating it is that it makes leaving easy. That is the
    /// point: we charge for the analysis, not for holding his data.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? owner_id = null)
    {
        var ownerId = ResolveOwnerId(owner_id);
        if (ownerId == null) return Forbid();

        var customers = await _db.Customers
            .Where(c => c.OwnerId == ownerId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        var stats = await ComputeStatsAsync(customers.Select(c => c.Id).ToList());

        var sb = new StringBuilder();
        // BOM: without it Excel on Windows opens UTF-8 as cp1256 and every Arabic name in
        // the file becomes mojibake — the export would technically work and be useless.
        sb.Append('﻿');
        sb.AppendLine("Name,Phone,Bookings,Attended,NoShows,LastVisit,AmountOwed,Status,Note");

        foreach (var c in customers)
        {
            var s = stats[c.Id];
            sb.AppendLine(string.Join(",",
                Csv(c.Name),
                CsvPhone(c.Phone),
                Csv(s.TotalBookings.ToString()),
                Csv(s.Attended.ToString()),
                Csv(s.NoShow.ToString()),
                Csv(s.LastVisit ?? ""),
                Csv(s.AmountOwed.ToString("0.###")),
                Csv(c.Status),
                Csv(c.Note)));
        }

        var stamp = PlatformConstants.JordanToday().ToString("yyyy-MM-dd");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"customers-{stamp}.csv");
    }

    /// <summary>
    /// Quotes a CSV field and defuses formula injection.
    ///
    /// A customer note beginning "=" or "+" is executed as a formula the moment the file is
    /// opened in Excel. The owner exports his own customers and runs whatever a customer
    /// once typed into a name field. Same helper as ReportsController.
    /// </summary>
    private static string Csv(string? value)
    {
        var s = value ?? "";
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>
    /// The phone column, which the generic guard above quietly ruined.
    ///
    /// Canonical numbers always begin "+962", so every single row was exported as
    /// <c>'+962791234567</c>. Excel treats the leading apostrophe as a text marker and
    /// hides it; Google Sheets, LibreOffice, pandas and any CRM re-import show it
    /// literally. The whole point of this endpoint is that the owner can take his list
    /// and walk out, and the one column that matters arrived broken everywhere except
    /// the single program that hides the damage.
    ///
    /// A value that survives <c>PhoneNormalizer</c> is digits and a leading '+' — it
    /// cannot carry a formula. Anything else (legacy junk, a mistyped row) still goes
    /// through the strict guard.
    /// </summary>
    private static string CsvPhone(string? value)
    {
        var s = value ?? "";
        return PhoneNormalizer.IsJordanianMobile(s)
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : Csv(s);
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
            if (c.Status == "active" && IsLapsedRule(lifetimeVisits, daysSince))
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
                Notes = b.Notes,
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

    /// <summary>
    /// GET /api/v1/customers/{id}/bookings — the full history, paginated. What the detail
    /// page's table reads; <see cref="Get"/>'s <c>recentBookings</c> stays capped at 50 for
    /// callers that just want a quick preview.
    /// </summary>
    [HttpGet("{id}/bookings")]
    public async Task<IActionResult> Bookings(
        string id,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Customer not found" });

        var ownerId = ResolveOwnerId(customer.OwnerId);
        if (ownerId == null || ownerId != customer.OwnerId) return Forbid();
        if (limit is < 1 or > 100) limit = 20;

        var query = _db.Bookings.Where(b => b.CustomerId == customer.Id);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(b => b.Status == status);

        var total = await query.CountAsync();
        var bookings = await query
            .Include(b => b.Venue)
            .OrderByDescending(b => b.Date).ThenByDescending(b => b.StartTime)
            .Skip((page - 1) * limit)
            .Take(limit)
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
                Notes = b.Notes,
            })
            .ToListAsync();

        return Ok(new ApiResponse<List<CustomerBookingItem>>
        {
            Data = bookings,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
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
        if (!CanWriteCustomers) return Forbid();

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
        if (!CanWriteCustomers) return Forbid();

        customer.Status = "archived";
        customer.ArchivedAt = DateTime.UtcNow;
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var stats = await ComputeStatsAsync([customer.Id]);
        return Ok(new ApiResponse<CustomerResponse> { Data = ToDto(customer, stats[customer.Id]), Message = "Archived" });
    }

    /// <summary>
    /// PATCH /api/v1/customers/{id}/restore — bring an archived customer back into the book.
    ///
    /// Archiving was one-way. An owner who archived the wrong Khalid — and there are several
    /// Khalids — had no route back: the list only ever queries Status == "active", so the row
    /// became invisible everywhere except the CSV export. The record was intact and
    /// unreachable, which is the worst of both.
    ///
    /// Nothing is recreated here. The row never left; only its status moves, so the phone
    /// number keeps its (owner, phone) uniqueness and every past booking stays attached.
    /// </summary>
    [HttpPatch("{id}/restore")]
    public async Task<IActionResult> Restore(string id)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id);
        if (customer == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Customer not found" });

        var ownerId = ResolveOwnerId(customer.OwnerId);
        if (ownerId == null || ownerId != customer.OwnerId) return Forbid();
        if (!CanWriteCustomers) return Forbid();

        customer.Status = "active";
        customer.ArchivedAt = null;
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var stats = await ComputeStatsAsync([customer.Id]);
        return Ok(new ApiResponse<CustomerResponse> { Data = ToDto(customer, stats[customer.Id]), Message = "Restored" });
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
