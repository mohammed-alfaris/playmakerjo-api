using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Notifications;
using SportsVenueApi.Models;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public NotificationsController(AppDbContext db) => _db = db;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";

    [HttpGet]
    public async Task<IActionResult> GetNotifications([FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        var query = _db.Notifications
            .Where(n => n.UserId == UserId)
            .OrderByDescending(n => n.CreatedAt);

        var total = await query.CountAsync();
        var unreadCount = await _db.Notifications.CountAsync(n => n.UserId == UserId && !n.IsRead);

        var notifications = await query
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(n => new NotificationResponse
            {
                Id = n.Id,
                Title = n.Title,
                Body = n.Body,
                Type = n.Type,
                ReferenceId = n.ReferenceId,
                IsRead = n.IsRead,
                CreatedAt = n.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data = new NotificationListResponse
            {
                Notifications = notifications,
                UnreadCount = unreadCount,
            },
            pagination = new { page, limit, total },
        });
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount()
    {
        var count = await _db.Notifications.CountAsync(n => n.UserId == UserId && !n.IsRead);
        return Ok(new ApiResponse<object> { Data = new { unreadCount = count } });
    }

    [HttpPatch("{id}/read")]
    public async Task<IActionResult> MarkAsRead(string id)
    {
        var notification = await _db.Notifications
            .FirstOrDefaultAsync(n => n.Id == id && n.UserId == UserId);

        if (notification == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Notification not found" });

        notification.IsRead = true;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Message = "Marked as read" });
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        await _db.Notifications
            .Where(n => n.UserId == UserId && !n.IsRead)
            .ExecuteUpdateAsync(n => n.SetProperty(x => x.IsRead, true));

        return Ok(new ApiResponse<object> { Message = "All marked as read" });
    }

    [HttpPost("device-token")]
    public async Task<IActionResult> RegisterDeviceToken([FromBody] RegisterDeviceTokenRequest req)
    {
        if (string.IsNullOrEmpty(req.Token))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Token is required" });

        // Upsert: update existing or create new
        var existing = await _db.DeviceTokens
            .FirstOrDefaultAsync(d => d.UserId == UserId && d.Token == req.Token);

        if (existing != null)
        {
            existing.IsActive = true;
            existing.Platform = req.Platform;
        }
        else
        {
            _db.DeviceTokens.Add(new DeviceToken
            {
                UserId = UserId,
                Token = req.Token,
                Platform = req.Platform,
            });
        }

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Message = "Device token registered" });
    }

    [HttpDelete("device-token")]
    public async Task<IActionResult> UnregisterDeviceToken([FromQuery] string token)
    {
        if (string.IsNullOrEmpty(token))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Token is required" });

        await _db.DeviceTokens
            .Where(d => d.UserId == UserId && d.Token == token)
            .ExecuteUpdateAsync(d => d.SetProperty(x => x.IsActive, false));

        return Ok(new ApiResponse<object> { Message = "Device token deactivated" });
    }
}
