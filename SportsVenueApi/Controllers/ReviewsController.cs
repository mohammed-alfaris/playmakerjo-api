using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Reviews;
using SportsVenueApi.Models;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/reviews")]
[Authorize]
public class ReviewsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReviewsController(AppDbContext db)
    {
        _db = db;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";

    private static ReviewResponse ToDto(Review r) => new()
    {
        Id = r.Id,
        PlayerId = r.PlayerId,
        PlayerName = r.Player?.Name ?? "",
        PlayerAvatar = r.Player?.Avatar,
        VenueId = r.VenueId,
        Rating = r.Rating,
        Comment = r.Comment,
        CreatedAt = r.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        UpdatedAt = r.UpdatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
    };

    /// <summary>Public: paginated, non-hidden reviews for a venue.</summary>
    [AllowAnonymous]
    [HttpGet("venue/{venueId}")]
    public async Task<IActionResult> ListForVenue(string venueId, [FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        var query = _db.Reviews
            .Where(r => r.VenueId == venueId && !r.Hidden);

        var total = await query.CountAsync();

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Include(r => r.Player)
            .AsSplitQuery()
            .ToListAsync();

        return Ok(new ApiResponse<List<ReviewResponse>>
        {
            Data = items.Select(ToDto).ToList(),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
        });
    }

    /// <summary>Current user's review for this venue (null if none).</summary>
    [HttpGet("my/{venueId}")]
    public async Task<IActionResult> GetMine(string venueId)
    {
        var review = await _db.Reviews
            .Where(r => r.VenueId == venueId && r.PlayerId == UserId)
            .Include(r => r.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync();

        if (review == null)
            return Ok(new ApiResponse<ReviewResponse?> { Data = null, Message = "No review yet" });

        return Ok(new ApiResponse<ReviewResponse> { Data = ToDto(review) });
    }

    /// <summary>Can the current user review this venue? Requires at least one completed booking.</summary>
    [HttpGet("eligibility/{venueId}")]
    public async Task<IActionResult> Eligibility(string venueId)
    {
        var canReview = await _db.Bookings
            .AnyAsync(b => b.VenueId == venueId && b.PlayerId == UserId && b.Status == "completed");

        var hasExistingReview = await _db.Reviews
            .AnyAsync(r => r.VenueId == venueId && r.PlayerId == UserId);

        return Ok(new ApiResponse<ReviewEligibilityResponse>
        {
            Data = new ReviewEligibilityResponse
            {
                CanReview = canReview,
                HasExistingReview = hasExistingReview,
            }
        });
    }

    /// <summary>Create a review. Requires a completed booking at this venue. One review per player per venue.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateReviewRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid request" });

        var venueExists = await _db.Venues.AnyAsync(v => v.Id == req.VenueId);
        if (!venueExists)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var hasCompleted = await _db.Bookings
            .AnyAsync(b => b.VenueId == req.VenueId && b.PlayerId == UserId && b.Status == "completed");
        if (!hasCompleted)
            return StatusCode(403, new ApiResponse<object>
            {
                Success = false,
                Message = "You can only review a venue after completing a booking there",
            });

        var existing = await _db.Reviews.AnyAsync(r => r.VenueId == req.VenueId && r.PlayerId == UserId);
        if (existing)
            return Conflict(new ApiResponse<object>
            {
                Success = false,
                Message = "You have already reviewed this venue. Edit your existing review instead.",
            });

        var review = new Review
        {
            PlayerId = UserId,
            VenueId = req.VenueId,
            Rating = req.Rating,
            Comment = req.Comment,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        _db.Reviews.Add(review);
        await _db.SaveChangesAsync();

        // Reload with player
        var created = await _db.Reviews
            .Include(r => r.Player)
            .AsSplitQuery()
            .FirstAsync(r => r.Id == review.Id);

        return Ok(new ApiResponse<ReviewResponse> { Data = ToDto(created), Message = "Review posted" });
    }

    /// <summary>Update own review (rating + comment).</summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateReviewRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid request" });

        var review = await _db.Reviews
            .Include(r => r.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == id);

        if (review == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Review not found" });

        if (review.PlayerId != UserId)
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "You can only edit your own review" });

        review.Rating = req.Rating;
        review.Comment = req.Comment;
        review.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<ReviewResponse> { Data = ToDto(review), Message = "Review updated" });
    }
}
