using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.Models;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/favorites")]
[Authorize]
public class FavoritesController : ControllerBase
{
    private readonly AppDbContext _db;

    public FavoritesController(AppDbContext db) => _db = db;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";

    /// <summary>List the current user's favorite venues.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        var query = _db.Favorites
            .Where(f => f.UserId == UserId)
            .OrderByDescending(f => f.CreatedAt);

        var total = await query.CountAsync();

        var rawItems = await query
            .Skip((page - 1) * limit)
            .Take(limit)
            .Include(f => f.Venue)
            .AsSplitQuery()
            .Select(f => new
            {
                f.Id,
                f.VenueId,
                f.CreatedAt,
                VenueId2 = f.Venue.Id,
                VenueName = f.Venue.Name,
                f.Venue.City,
                f.Venue.SportsJson,
                f.Venue.PricePerHour,
                f.Venue.ImagesJson,
                f.Venue.Status,
            })
            .ToListAsync();

        var items = rawItems.Select(f =>
        {
            string? coverImage = null;
            if (f.ImagesJson != null)
            {
                var images = System.Text.Json.JsonSerializer.Deserialize<List<string>>(f.ImagesJson);
                coverImage = images?.FirstOrDefault();
            }
            return new
            {
                id = f.Id,
                venueId = f.VenueId,
                createdAt = f.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                venue = new
                {
                    id = f.VenueId2,
                    name = f.VenueName,
                    city = f.City,
                    sports = f.SportsJson,
                    pricePerHour = f.PricePerHour,
                    coverImage,
                    status = f.Status,
                },
            };
        }).ToList();

        return Ok(new ApiResponse<object>
        {
            Data = items,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
        });
    }

    /// <summary>Add a venue to favorites.</summary>
    [HttpPost("{venueId}")]
    public async Task<IActionResult> Add(string venueId)
    {
        var venueExists = await _db.Venues.AnyAsync(v => v.Id == venueId);
        if (!venueExists)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var exists = await _db.Favorites.AnyAsync(f => f.UserId == UserId && f.VenueId == venueId);
        if (exists)
            return Ok(new ApiResponse<object> { Message = "Already in favorites" });

        _db.Favorites.Add(new Favorite { UserId = UserId, VenueId = venueId });
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Message = "Added to favorites" });
    }

    /// <summary>Remove a venue from favorites.</summary>
    [HttpDelete("{venueId}")]
    public async Task<IActionResult> Remove(string venueId)
    {
        var deleted = await _db.Favorites
            .Where(f => f.UserId == UserId && f.VenueId == venueId)
            .ExecuteDeleteAsync();

        return Ok(new ApiResponse<object> { Message = deleted > 0 ? "Removed from favorites" : "Not in favorites" });
    }

    /// <summary>Batch check which venue IDs are favorited by the current user.</summary>
    [HttpGet("check")]
    public async Task<IActionResult> Check([FromQuery] string venueIds)
    {
        if (string.IsNullOrEmpty(venueIds))
            return Ok(new ApiResponse<object> { Data = new Dictionary<string, bool>() });

        var ids = venueIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var favorited = await _db.Favorites
            .Where(f => f.UserId == UserId && ids.Contains(f.VenueId))
            .Select(f => f.VenueId)
            .ToListAsync();

        var result = ids.ToDictionary(id => id, id => favorited.Contains(id));

        return Ok(new ApiResponse<object> { Data = result });
    }
}
