using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/venues")]
[Authorize]
public class VenuesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly string _uploadsBaseUrl;

    public VenuesController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _uploadsBaseUrl = config["Uploads:BaseUrl"]?.TrimEnd('/') ?? "";
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    private VenueResponse ToDto(Venue v) => new()
    {
        Id = v.Id,
        Name = v.Name,
        Owner = new OwnerRef { Id = v.Owner.Id, Name = v.Owner.Name },
        Sports = v.Sports,
        City = v.City,
        Address = v.Address,
        PricePerHour = v.PricePerHour,
        Status = v.Status,
        Description = v.Description,
        Images = v.Images?.Select(x => UploadUrlHelper.Normalize(x, _uploadsBaseUrl)).ToList()!,
        Latitude = v.Latitude,
        Longitude = v.Longitude,
        CliqAlias = v.CliqAlias,
        OperatingHours = v.OperatingHours,
        MinBookingDuration = v.MinBookingDuration,
        MaxBookingDuration = v.MaxBookingDuration,
        DepositPercentage = v.DepositPercentage,
        CreatedAt = v.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
    };

    /// <summary>Stamp averageRating + reviewCount onto a batch of venue DTOs from non-hidden reviews.</summary>
    private async Task StampAggregatesAsync(List<VenueResponse> dtos)
    {
        if (dtos.Count == 0) return;
        var ids = dtos.Select(d => d.Id).ToList();
        var stats = await _db.Reviews
            .Where(r => ids.Contains(r.VenueId) && !r.Hidden)
            .GroupBy(r => r.VenueId)
            .Select(g => new { VenueId = g.Key, Avg = g.Average(x => (double)x.Rating), Count = g.Count() })
            .ToListAsync();
        var map = stats.ToDictionary(s => s.VenueId, s => s);
        foreach (var d in dtos)
        {
            if (map.TryGetValue(d.Id, out var s))
            {
                d.AverageRating = Math.Round(s.Avg, 2);
                d.ReviewCount = s.Count;
            }
            else
            {
                d.AverageRating = null;
                d.ReviewCount = 0;
            }
        }
    }

    /// <summary>Stamp averageRating + reviewCount on a single venue DTO.</summary>
    private async Task StampAggregateAsync(VenueResponse dto)
    {
        var stat = await _db.Reviews
            .Where(r => r.VenueId == dto.Id && !r.Hidden)
            .GroupBy(r => r.VenueId)
            .Select(g => new { Avg = g.Average(x => (double)x.Rating), Count = g.Count() })
            .FirstOrDefaultAsync();

        if (stat == null)
        {
            dto.AverageRating = null;
            dto.ReviewCount = 0;
        }
        else
        {
            dto.AverageRating = Math.Round(stat.Avg, 2);
            dto.ReviewCount = stat.Count;
        }
    }

    // ── Public endpoints (no auth required) ──

    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<IActionResult> PublicList(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? sport = null,
        [FromQuery] string? city = null)
    {
        var baseQuery = _db.Venues.Where(v => v.Status == "active");

        if (!string.IsNullOrEmpty(search))
            baseQuery = baseQuery.Where(v => EF.Functions.Like(v.Name, $"%{search}%")
                                  || EF.Functions.Like(v.City!, $"%{search}%"));

        if (!string.IsNullOrEmpty(sport))
            baseQuery = baseQuery.Where(v => v.SportsJson.Contains($"\"{sport}\""));

        if (!string.IsNullOrEmpty(city))
            baseQuery = baseQuery.Where(v => v.City == city);

        var total = await baseQuery.CountAsync();
        var venues = await baseQuery
            .Include(v => v.Owner)
            .AsSplitQuery()
            .OrderByDescending(v => v.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var dtos = venues.Select(ToDto).ToList();
        await StampAggregatesAsync(dtos);

        return Ok(new ApiResponse<List<VenueResponse>>
        {
            Data = dtos,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    [AllowAnonymous]
    [HttpGet("public/{venueId}")]
    public async Task<IActionResult> PublicGet(string venueId)
    {
        var venue = await _db.Venues
            .Include(v => v.Owner)
            .FirstOrDefaultAsync(v => v.Id == venueId && v.Status == "active");

        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var dto = ToDto(venue);
        await StampAggregateAsync(dto);
        return Ok(new ApiResponse<VenueResponse> { Data = dto });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? sport = null,
        [FromQuery] string? status = null,
        [FromQuery] string? owner_id = null)
    {
        var ownerId = owner_id;
        if (UserRole == "venue_owner")
            ownerId = UserId;

        var baseQuery = _db.Venues.AsQueryable();

        if (!string.IsNullOrEmpty(ownerId))
            baseQuery = baseQuery.Where(v => v.OwnerId == ownerId);

        if (!string.IsNullOrEmpty(search))
            baseQuery = baseQuery.Where(v => EF.Functions.Like(v.Name, $"%{search}%")
                                  || EF.Functions.Like(v.City!, $"%{search}%"));

        if (!string.IsNullOrEmpty(sport))
            baseQuery = baseQuery.Where(v => v.SportsJson.Contains($"\"{sport}\""));

        if (!string.IsNullOrEmpty(status))
            baseQuery = baseQuery.Where(v => v.Status == status);

        var total = await baseQuery.CountAsync();
        var venues = await baseQuery
            .Include(v => v.Owner)
            .AsSplitQuery()
            .OrderByDescending(v => v.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var dtos = venues.Select(ToDto).ToList();
        await StampAggregatesAsync(dtos);

        return Ok(new ApiResponse<List<VenueResponse>>
        {
            Data = dtos,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] VenueCreateRequest req)
    {
        var ownerId = req.OwnerId ?? UserId;
        if (UserRole == "venue_owner")
            ownerId = UserId;

        var venue = new Venue
        {
            Name = req.Name,
            OwnerId = ownerId,
            Sports = req.Sports,
            City = req.City,
            Address = req.Address,
            PricePerHour = req.PricePerHour,
            Status = req.Status,
            Description = req.Description,
            Images = req.Images,
            Latitude = req.Latitude,
            Longitude = req.Longitude
        };

        _db.Venues.Add(venue);
        await _db.SaveChangesAsync();

        // Reload with owner
        var created = await _db.Venues.Include(v => v.Owner).FirstAsync(v => v.Id == venue.Id);

        return Ok(new ApiResponse<VenueResponse> { Data = ToDto(created), Message = "Venue created" });
    }

    [HttpGet("{venueId}")]
    public async Task<IActionResult> Get(string venueId)
    {
        var venue = await _db.Venues.Include(v => v.Owner).FirstOrDefaultAsync(v => v.Id == venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var dto = ToDto(venue);
        await StampAggregateAsync(dto);
        return Ok(new ApiResponse<VenueResponse> { Data = dto });
    }

    [HttpPatch("{venueId}")]
    public async Task<IActionResult> Update(string venueId, [FromBody] VenueUpdateRequest req)
    {
        var venue = await _db.Venues.Include(v => v.Owner).FirstOrDefaultAsync(v => v.Id == venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (req.Name != null) venue.Name = req.Name;
        if (req.OwnerId != null) venue.OwnerId = req.OwnerId;
        if (req.Sports != null) venue.Sports = req.Sports;
        if (req.City != null) venue.City = req.City;
        if (req.Address != null) venue.Address = req.Address;
        if (req.PricePerHour.HasValue) venue.PricePerHour = req.PricePerHour.Value;
        if (req.Status != null) venue.Status = req.Status;
        if (req.Description != null) venue.Description = req.Description;
        if (req.Images != null) venue.Images = req.Images;
        if (req.Latitude.HasValue) venue.Latitude = req.Latitude;
        if (req.Longitude.HasValue) venue.Longitude = req.Longitude;
        if (req.CliqAlias != null) venue.CliqAlias = req.CliqAlias;
        if (req.OperatingHours != null) venue.OperatingHoursJson = JsonSerializer.Serialize(req.OperatingHours);
        if (req.MinBookingDuration.HasValue) venue.MinBookingDuration = req.MinBookingDuration.Value;
        if (req.MaxBookingDuration.HasValue) venue.MaxBookingDuration = req.MaxBookingDuration.Value;
        if (req.DepositPercentage.HasValue) venue.DepositPercentage = req.DepositPercentage.Value;

        await _db.SaveChangesAsync();

        // Reload owner if changed
        if (req.OwnerId != null)
            await _db.Entry(venue).Reference(v => v.Owner).LoadAsync();

        return Ok(new ApiResponse<VenueResponse> { Data = ToDto(venue), Message = "Venue updated" });
    }

    [HttpDelete("{venueId}")]
    public async Task<IActionResult> Delete(string venueId)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        _db.Venues.Remove(venue);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Data = null, Message = "Venue deleted" });
    }

    // GET /api/v1/venues/{venueId}/available-slots?date=2025-04-10
    [AllowAnonymous]
    [HttpGet("{venueId}/available-slots")]
    public async Task<IActionResult> AvailableSlots(string venueId, [FromQuery] string? date = null)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date, out var bookingDate))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid or missing date. Use YYYY-MM-DD" });

        // Get operating hours for the requested day
        var dayName = bookingDate.DayOfWeek.ToString().ToLower()[..3];
        OperatingHoursInfo? hours = null;

        var operatingHours = venue.OperatingHours;
        if (operatingHours != null && operatingHours.TryGetValue(dayName, out var dayHoursObj))
        {
            var dayHoursJson = JsonSerializer.Serialize(dayHoursObj);
            var dayHours = JsonSerializer.Deserialize<Dictionary<string, string>>(dayHoursJson);
            if (dayHours != null)
            {
                hours = new OperatingHoursInfo
                {
                    Open = dayHours.GetValueOrDefault("open", "08:00"),
                    Close = dayHours.GetValueOrDefault("close", "22:00")
                };
            }
        }

        // Get existing bookings for that day (non-cancelled)
        var existingBookings = await _db.Bookings
            .Where(b => b.VenueId == venueId
                && b.Date.Date == bookingDate.Date
                && b.Status != "cancelled")
            .ToListAsync();

        var bookedSlots = existingBookings
            .Where(b => !string.IsNullOrEmpty(b.StartTime))
            .Select(b => new BookedSlotInfo
            {
                StartTime = b.StartTime!,
                Duration = b.Duration,
                Sport = b.Sport
            })
            .OrderBy(s => s.StartTime)
            .ToList();

        return Ok(new ApiResponse<AvailableSlotsResponse>
        {
            Data = new AvailableSlotsResponse
            {
                VenueId = venueId,
                Date = bookingDate.ToString("yyyy-MM-dd"),
                OperatingHours = hours,
                BookedSlots = bookedSlots,
                PricePerHour = venue.PricePerHour,
                MinDuration = venue.MinBookingDuration,
                MaxDuration = venue.MaxBookingDuration,
                DepositPercentage = venue.DepositPercentage
            }
        });
    }

    [HttpGet("{venueId}/stats")]
    public async Task<IActionResult> Stats(string venueId)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var totalBookings = await _db.Bookings.CountAsync(b => b.VenueId == venueId);
        var totalRevenue = await _db.Bookings
            .Where(b => b.VenueId == venueId && b.Status == "completed")
            .SumAsync(b => b.Amount);

        return Ok(new ApiResponse<VenueStatsResponse>
        {
            Data = new VenueStatsResponse
            {
                TotalBookings = totalBookings,
                TotalRevenue = totalRevenue,
                ActiveSince = venue.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
            }
        });
    }
}
