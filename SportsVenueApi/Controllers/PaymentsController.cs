using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Payments;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/payments")]
[Authorize]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _db;

    public PaymentsController(AppDbContext db) => _db = db;

    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null)
    {
        if (UserRole != "super_admin")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Admin only" });

        var baseQuery = _db.Payments.AsQueryable();

        if (!string.IsNullOrEmpty(status))
            baseQuery = baseQuery.Where(p => p.Status == status);

        var total = await baseQuery.CountAsync();
        var payments = await baseQuery
            .Include(p => p.Player)
            .AsSplitQuery()
            .OrderByDescending(p => p.Date)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = payments.Select(p => new PaymentResponse
        {
            Id = p.Id,
            BookingRef = p.BookingId,
            Player = new PaymentPlayerRef { Id = p.Player.Id, Name = p.Player.Name },
            Amount = p.Amount,
            Method = p.Method,
            Status = p.Status,
            Date = p.Date.ToString("yyyy-MM-ddTHH:mm:ssZ")
        }).ToList();

        return Ok(new ApiResponse<List<PaymentResponse>>
        {
            Data = data,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }
}
