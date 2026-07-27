using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Payments;
using SportsVenueApi.Models;

namespace SportsVenueApi.Controllers;

/// <summary>
/// The money ledger — every recorded receipt, newest first.
///
/// This used to be admin-only, which made sense when the only reader was the platform
/// checking its own cut. Now that the product is sold to the venue, the owner is the
/// person who most needs it: it is the answer to "what did we take yesterday", "who on my
/// staff recorded it", and "did that CliQ transfer actually get approved". The scoping
/// below is therefore deny-by-default — an unrecognised role sees an empty ledger, never
/// somebody else's.
/// </summary>
[ApiController]
[Route("api/v1/payments")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PaymentsController(AppDbContext db) => _db = db;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";
    private string? StaffOwnerId => User.FindFirstValue("owner_id");

    /// <summary>
    /// Narrow the ledger to what the caller is entitled to see, or return null to mean
    /// "nothing at all". Scoping goes through the booking's venue rather than the payment's
    /// denormalised venue_id, because rows written before that column existed carry null
    /// there and would otherwise slip out of every filter — including, dangerously, an
    /// owner's filter.
    /// </summary>
    private IQueryable<Payment>? Scope(IQueryable<Payment> q, string? ownerId, string? venueId)
    {
        switch (UserRole)
        {
            case "super_admin":
                if (!string.IsNullOrWhiteSpace(ownerId))
                    q = q.Where(p => p.Booking.Venue.OwnerId == ownerId);
                break;

            case "venue_owner":
                q = q.Where(p => p.Booking.Venue.OwnerId == UserId);
                break;

            case "venue_staff":
                if (string.IsNullOrEmpty(StaffOwnerId)) return null;
                q = q.Where(p => p.Booking.Venue.OwnerId == StaffOwnerId);
                break;

            case "player":
                // A player may see receipts for their own bookings and nothing else.
                q = q.Where(p => p.PlayerId == UserId);
                break;

            default:
                return null;
        }

        if (!string.IsNullOrWhiteSpace(venueId))
            q = q.Where(p => p.Booking.VenueId == venueId);

        return q;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? method = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery(Name = "owner_id")] string? ownerId = null,
        [FromQuery(Name = "venue_id")] string? venueId = null)
    {
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        var scoped = Scope(_db.Payments.AsQueryable(), ownerId, venueId);
        if (scoped == null)
            return Ok(new ApiResponse<List<PaymentResponse>>
            {
                Data = [],
                Pagination = new PaginationInfo { Page = page, Limit = limit, Total = 0 }
            });

        scoped = ApplyFilters(scoped, status, method, from, to);

        var total = await scoped.CountAsync();
        var payments = await scoped
            .Include(p => p.Player)
            .Include(p => p.Customer)
            .Include(p => p.RecordedBy)
            .Include(p => p.Booking).ThenInclude(b => b.Venue)
            .AsSplitQuery()
            .OrderByDescending(p => p.Date)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        return Ok(new ApiResponse<List<PaymentResponse>>
        {
            Data = payments.Select(ToDto).ToList(),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    /// <summary>
    /// GET /api/v1/payments/totals — the same filter, summed.
    ///
    /// Computed over the whole filtered set rather than the current page, because a total
    /// that silently means "this page only" is the kind of number an owner reconciles his
    /// cash box against once, finds wrong, and never trusts again.
    /// </summary>
    [HttpGet("totals")]
    public async Task<IActionResult> Totals(
        [FromQuery] string? status = null,
        [FromQuery] string? method = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery(Name = "owner_id")] string? ownerId = null,
        [FromQuery(Name = "venue_id")] string? venueId = null)
    {
        var scoped = Scope(_db.Payments.AsQueryable(), ownerId, venueId);
        if (scoped == null)
            return Ok(new ApiResponse<PaymentTotalsResponse> { Data = new PaymentTotalsResponse() });

        scoped = ApplyFilters(scoped, status, method, from, to);

        var rows = await scoped
            .Select(p => new { p.Amount, p.Method })
            .ToListAsync();

        return Ok(new ApiResponse<PaymentTotalsResponse>
        {
            Data = new PaymentTotalsResponse
            {
                Count = rows.Count,
                Total = Math.Round(rows.Sum(r => r.Amount), 3),
                ByMethod = rows
                    .GroupBy(r => string.IsNullOrEmpty(r.Method) ? "other" : r.Method!)
                    .ToDictionary(g => g.Key, g => Math.Round(g.Sum(r => r.Amount), 3)),
            }
        });
    }

    private static IQueryable<Payment> ApplyFilters(
        IQueryable<Payment> q, string? status, string? method, string? from, string? to)
    {
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(method)) q = q.Where(p => p.Method == method);

        if (DateTime.TryParse(from, out var fromDate))
            q = q.Where(p => p.Date >= fromDate.Date);

        // Inclusive of the end day: a range ending "today" that excluded today's takings
        // would quietly under-report every time the owner checked.
        if (DateTime.TryParse(to, out var toDate))
            q = q.Where(p => p.Date < toDate.Date.AddDays(1));

        return q;
    }

    private static PaymentResponse ToDto(Payment p) => new()
    {
        Id = p.Id,
        BookingRef = p.BookingId,
        Player = new PaymentPlayerRef { Id = p.Player?.Id ?? p.PlayerId, Name = p.Player?.Name ?? "" },
        Customer = p.Customer == null ? null : new PaymentPlayerRef { Id = p.Customer.Id, Name = p.Customer.Name },
        // The customer is the payer whenever one is known; the player account is the
        // fallback. Resolved here so no client has to repeat the rule.
        PayerName = p.Customer?.Name ?? p.Player?.Name ?? "",
        RecordedBy = p.RecordedBy == null ? null : new PaymentPlayerRef { Id = p.RecordedBy.Id, Name = p.RecordedBy.Name },
        Venue = p.Booking?.Venue == null ? null : new PaymentPlayerRef { Id = p.Booking.Venue.Id, Name = p.Booking.Venue.Name },
        Amount = p.Amount,
        Method = p.Method,
        Kind = p.Kind,
        Status = p.Status,
        Note = p.Note,
        Date = p.Date.ToString("yyyy-MM-ddTHH:mm:ssZ")
    };
}
