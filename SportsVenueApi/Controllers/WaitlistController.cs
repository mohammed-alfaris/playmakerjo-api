using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Leads;
using SportsVenueApi.Models;
using SportsVenueApi.Services;
using System.ComponentModel.DataAnnotations;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/waitlist")]
public class WaitlistController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WaitlistController> _logger;
    private readonly NotificationService _notifications;

    public WaitlistController(AppDbContext db, ILogger<WaitlistController> logger, NotificationService notifications)
    {
        _db = db;
        _logger = logger;
        _notifications = notifications;
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

        // The lead is saved; telling the admins is a courtesy on top and must not fail the form.
        try { await _notifications.NotifyNewVenueLead(entry); }
        catch (Exception ex) { _logger.LogWarning(ex, "New-lead notification failed for {VenueName}", entry.VenueName); }
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

    private static readonly string[] Stages = ["new", "contacted", "demo", "trial", "won", "lost"];
    private static readonly string[] OpenStages = ["new", "contacted", "demo", "trial"];

    // GET /api/v1/waitlist/venues?status= — super_admin only. Newest first; "due" lists the
    // leads whose follow-up date has come, soonest first.
    [HttpGet("venues")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> GetVenues([FromQuery] int page = 1, [FromQuery] int limit = 50, [FromQuery] string? status = null)
    {
        if (page < 1) page = 1;
        if (limit is < 1 or > 200) limit = 50;
        var today = PlatformConstants.JordanToday();

        var q = _db.VenueWaitlist.AsNoTracking().AsQueryable();
        if (status == "due")
            q = q.Where(v => OpenStages.Contains(v.Status) && v.NextFollowUpOn != null && v.NextFollowUpOn <= today)
                 .OrderBy(v => v.NextFollowUpOn).ThenByDescending(v => v.CreatedAt);
        else
        {
            if (!string.IsNullOrEmpty(status)) q = q.Where(v => v.Status == status);
            q = q.OrderByDescending(v => v.CreatedAt);
        }

        var total = await q.CountAsync();
        var rows = await q.Skip((page - 1) * limit).Take(limit).ToListAsync();

        return Ok(new ApiResponse<List<VenueLeadResponse>>
        {
            Data = rows.Select(r => ToDto(r, today)).ToList(),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
        });
    }

    // GET /api/v1/waitlist/venues/stats — how many leads sit at each stage, and how many are due a call.
    [HttpGet("venues/stats")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> GetVenueStats()
    {
        var today = PlatformConstants.JordanToday();
        var counts = await _db.VenueWaitlist.AsNoTracking()
            .GroupBy(v => v.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();
        var due = await _db.VenueWaitlist.CountAsync(v =>
            OpenStages.Contains(v.Status) && v.NextFollowUpOn != null && v.NextFollowUpOn <= today);

        return Ok(new ApiResponse<LeadStats>
        {
            Data = new LeadStats
            {
                ByStatus = Stages.ToDictionary(st => st, st => counts.FirstOrDefault(c => c.Key == st)?.Count ?? 0),
                FollowUpsDue = due,
            },
        });
    }

    // PATCH /api/v1/waitlist/venues/{id} — move a lead along: stage, notes, next call, why lost, who it became.
    [HttpPatch("venues/{id:int}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> UpdateVenueLead(int id, [FromBody] UpdateVenueLeadRequest req)
    {
        var lead = await _db.VenueWaitlist.FirstOrDefaultAsync(v => v.Id == id);
        if (lead == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Lead not found" });

        if (req.Status != null)
        {
            if (!Stages.Contains(req.Status))
                return BadRequest(new ApiResponse<object> { Success = false, Message = "status must be new, contacted, demo, trial, won or lost" });
            lead.Status = req.Status;
        }
        if (req.Notes != null) lead.Notes = req.Notes.Length == 0 ? null : req.Notes;
        if (req.NextFollowUpOn != null)
        {
            if (req.NextFollowUpOn.Length == 0) lead.NextFollowUpOn = null;
            else if (DateTime.TryParseExact(req.NextFollowUpOn, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out var d))
                lead.NextFollowUpOn = d.Date;
            else
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Use yyyy-MM-dd for the follow-up date" });
        }
        if (req.LostReason != null) lead.LostReason = req.LostReason.Length == 0 ? null : req.LostReason.Trim();
        if (req.ConvertedOwnerId != null)
        {
            if (!await _db.Users.AnyAsync(u => u.Id == req.ConvertedOwnerId && u.Role == "venue_owner"))
                return BadRequest(new ApiResponse<object> { Success = false, Message = "That account is not a venue owner" });
            lead.ConvertedOwnerId = req.ConvertedOwnerId;
            lead.Status = "won";
        }
        // A closed lead has nobody to call back.
        if (lead.Status is "won" or "lost") lead.NextFollowUpOn = null;

        lead.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<VenueLeadResponse> { Data = ToDto(lead, PlatformConstants.JordanToday()), Message = "Lead updated" });
    }

    private static VenueLeadResponse ToDto(VenueWaitlist v, DateTime today) => new()
    {
        Id = v.Id,
        ContactName = v.ContactName,
        VenueName = v.VenueName,
        City = v.City,
        Phone = v.Phone,
        Email = v.Email,
        SportsJson = v.SportsJson,
        Sports = v.Sports,
        CreatedAt = v.CreatedAt,
        Status = v.Status,
        Notes = v.Notes,
        NextFollowUpOn = v.NextFollowUpOn?.ToString("yyyy-MM-dd"),
        FollowUpDue = OpenStages.Contains(v.Status) && v.NextFollowUpOn != null && v.NextFollowUpOn <= today,
        LostReason = v.LostReason,
        ConvertedOwnerId = v.ConvertedOwnerId,
        UpdatedAt = v.UpdatedAt,
    };
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
