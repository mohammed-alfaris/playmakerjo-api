using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.Models;
using System.ComponentModel.DataAnnotations;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/waitlist")]
public class WaitlistController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WaitlistController> _logger;

    public WaitlistController(AppDbContext db, ILogger<WaitlistController> logger)
    {
        _db = db;
        _logger = logger;
    }

    // POST /api/v1/waitlist/player
    [HttpPost("player")]
    [AllowAnonymous]
    public async Task<IActionResult> JoinPlayerWaitlist([FromBody] PlayerWaitlistRequest req)
    {
        var email = req.Email.Trim().ToLowerInvariant();

        var exists = await _db.PlayerWaitlist.AnyAsync(p => p.Email == email);
        if (exists)
            return Ok(new ApiResponse<object> { Message = "Already on the waitlist." });

        _db.PlayerWaitlist.Add(new PlayerWaitlist { Email = email });
        await _db.SaveChangesAsync();

        _logger.LogInformation("Player joined waitlist: {Email}", email);
        return Ok(new ApiResponse<object> { Message = "You're on the list!" });
    }

    // POST /api/v1/waitlist/venue
    [HttpPost("venue")]
    [AllowAnonymous]
    public async Task<IActionResult> RegisterVenue([FromBody] VenueWaitlistRequest req)
    {
        var email = req.Email.Trim().ToLowerInvariant();

        var exists = await _db.VenueWaitlist.AnyAsync(v => v.Email == email);
        if (exists)
            return Ok(new ApiResponse<object> { Message = "Already registered." });

        var entry = new VenueWaitlist
        {
            ContactName = req.ContactName.Trim(),
            VenueName   = req.VenueName.Trim(),
            City        = req.City.Trim(),
            Phone       = req.Phone.Trim(),
            Email       = email,
        };
        entry.Sports = req.Sports ?? [];

        _db.VenueWaitlist.Add(entry);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Venue registered on waitlist: {VenueName} ({Email})", entry.VenueName, email);
        return Ok(new ApiResponse<object> { Message = "Registered! We'll be in touch." });
    }

    // GET /api/v1/waitlist/players — super_admin only
    [HttpGet("players")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> GetPlayers([FromQuery] int page = 1, [FromQuery] int limit = 50)
    {
        var total = await _db.PlayerWaitlist.CountAsync();
        var items = await _db.PlayerWaitlist
            .OrderByDescending(p => p.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(p => new { p.Id, p.Email, p.CreatedAt })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Data = items, Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total } });
    }

    // GET /api/v1/waitlist/venues — super_admin only
    [HttpGet("venues")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> GetVenues([FromQuery] int page = 1, [FromQuery] int limit = 50)
    {
        var total = await _db.VenueWaitlist.CountAsync();
        var items = await _db.VenueWaitlist
            .OrderByDescending(v => v.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(v => new
            {
                v.Id,
                v.ContactName,
                v.VenueName,
                v.City,
                v.Phone,
                v.Email,
                v.SportsJson,
                v.CreatedAt,
            })
            .ToListAsync();

        return Ok(new ApiResponse<object> { Data = items, Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total } });
    }
}

public class PlayerWaitlistRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = "";
}

public class VenueWaitlistRequest
{
    [Required, MinLength(2)]
    public string ContactName { get; set; } = "";

    [Required, MinLength(2)]
    public string VenueName { get; set; } = "";

    [Required]
    public string City { get; set; } = "";

    [Required, MinLength(7)]
    public string Phone { get; set; } = "";

    [Required, EmailAddress]
    public string Email { get; set; } = "";

    public List<string>? Sports { get; set; }
}
