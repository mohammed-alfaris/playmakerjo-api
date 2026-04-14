using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Notifications;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<NotificationsController> _logger;

    private readonly string _uploadsBaseUrl;

    public NotificationsController(AppDbContext db, NotificationService notifications, ILogger<NotificationsController> logger, IConfiguration config)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
        _uploadsBaseUrl = config["Uploads:BaseUrl"]?.TrimEnd('/') ?? "";
    }

    private string? NormalizeUploadUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http")) return url;
        var idx = url.IndexOf("/uploads/");
        if (idx < 0) return url;
        return _uploadsBaseUrl + url[idx..];
    }

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

        return Ok(new ApiResponse<NotificationListResponse>
        {
            Data = new NotificationListResponse
            {
                Notifications = notifications,
                UnreadCount = unreadCount,
            },
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
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

    // ══════════════════════════════════════════════════════════════════════
    // Admin: Send notification to a specific user
    // ══════════════════════════════════════════════════════════════════════

    [HttpGet("users")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> GetUsersWithFcm(
        [FromQuery] string? search,
        [FromQuery] string? role,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 50)
    {
        var query = _db.Users.AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => u.Name.Contains(search) || u.Email.Contains(search));

        if (!string.IsNullOrEmpty(role))
            query = query.Where(u => u.Role == role);

        var total = await query.CountAsync();

        // Get active device token info grouped by user
        var activeTokens = await _db.DeviceTokens
            .Where(d => d.IsActive)
            .GroupBy(d => d.UserId)
            .Select(g => new { UserId = g.Key, Platforms = g.Select(d => d.Platform).Distinct().ToList() })
            .ToDictionaryAsync(g => g.UserId, g => g.Platforms);

        var users = await query
            .OrderBy(u => u.Name)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(u => new UserWithFcmResponse
            {
                Id = u.Id,
                Name = u.Name,
                Email = u.Email,
                Phone = u.Phone,
                Role = u.Role,
                Status = u.Status,
                Avatar = u.Avatar, // normalized below
            })
            .ToListAsync();

        // Normalize avatar URLs
        foreach (var u in users)
            u.Avatar = NormalizeUploadUrl(u.Avatar);

        // Enrich with FCM data
        foreach (var u in users)
        {
            if (activeTokens.TryGetValue(u.Id, out var platforms))
            {
                u.HasFcm = true;
                u.FcmPlatforms = platforms;
            }
        }

        return Ok(new ApiResponse<List<UserWithFcmResponse>>
        {
            Data = users,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
        });
    }

    [HttpPost("send")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> AdminSendNotification([FromBody] AdminSendNotificationRequest req)
    {
        if (req.UserIds.Count == 0 || string.IsNullOrEmpty(req.Title) || string.IsNullOrEmpty(req.Body))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "userIds, title, and body are required" });

        var existingIds = await _db.Users
            .Where(u => req.UserIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync();

        if (existingIds.Count == 0)
            return NotFound(new ApiResponse<object> { Success = false, Message = "No valid users found" });

        var sent = 0;
        foreach (var uid in existingIds)
        {
            try
            {
                await _notifications.CreateNotification(uid, req.Title, req.Body, req.Type, image: req.Image);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify user {UserId}", uid);
            }
        }

        return Ok(new ApiResponse<object>
        {
            Data = new { sent, total = existingIds.Count },
            Message = $"Notification sent to {sent} user(s)",
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Admin: Notification Templates CRUD
    // ══════════════════════════════════════════════════════════════════════

    [HttpGet("templates")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> GetTemplates()
    {
        var templates = await _db.NotificationTemplates
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new TemplateResponse
            {
                Id = t.Id,
                Name = t.Name,
                Title = t.Title,
                Body = t.Body,
                Type = t.Type,
                CreatedAt = t.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            })
            .ToListAsync();

        return Ok(new ApiResponse<List<TemplateResponse>> { Data = templates });
    }

    [HttpPost("templates")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> CreateTemplate([FromBody] CreateTemplateRequest req)
    {
        if (string.IsNullOrEmpty(req.Name) || string.IsNullOrEmpty(req.Title) || string.IsNullOrEmpty(req.Body))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "name, title, and body are required" });

        var template = new NotificationTemplate
        {
            Name = req.Name,
            Title = req.Title,
            Body = req.Body,
            Type = req.Type,
        };

        _db.NotificationTemplates.Add(template);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<TemplateResponse>
        {
            Data = new TemplateResponse
            {
                Id = template.Id,
                Name = template.Name,
                Title = template.Title,
                Body = template.Body,
                Type = template.Type,
                CreatedAt = template.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
            Message = "Template created",
        });
    }

    [HttpPatch("templates/{id}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> UpdateTemplate(string id, [FromBody] UpdateTemplateRequest req)
    {
        var template = await _db.NotificationTemplates.FindAsync(id);
        if (template == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Template not found" });

        if (req.Name != null) template.Name = req.Name;
        if (req.Title != null) template.Title = req.Title;
        if (req.Body != null) template.Body = req.Body;
        if (req.Type != null) template.Type = req.Type;

        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<TemplateResponse>
        {
            Data = new TemplateResponse
            {
                Id = template.Id,
                Name = template.Name,
                Title = template.Title,
                Body = template.Body,
                Type = template.Type,
                CreatedAt = template.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            },
            Message = "Template updated",
        });
    }

    [HttpDelete("templates/{id}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> DeleteTemplate(string id)
    {
        var deleted = await _db.NotificationTemplates
            .Where(t => t.Id == id)
            .ExecuteDeleteAsync();

        if (deleted == 0)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Template not found" });

        return Ok(new ApiResponse<object> { Message = "Template deleted" });
    }
}
