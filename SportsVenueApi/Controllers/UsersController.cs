using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Auth;
using SportsVenueApi.DTOs.Users;
using BCrypt.Net;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly string _uploadsBaseUrl;

    public UsersController(AppDbContext db, IConfiguration config)
    {
        _db = db;
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
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    private UserResponse ToDto(Models.User u) => new()
    {
        Id = u.Id,
        Name = u.Name,
        Email = u.Email,
        Phone = u.Phone,
        Role = u.Role,
        Status = u.Status,
        Avatar = NormalizeUploadUrl(u.Avatar),
        Permissions = u.Permissions,
        CreatedAt = u.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
    };

    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await _db.Users.FindAsync(UserId);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        return Ok(new ApiResponse<UserResponse> { Data = ToDto(user), Message = "OK" });
    }

    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateProfileRequest req)
    {
        var user = await _db.Users.FindAsync(UserId);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        if (req.Name != null) user.Name = req.Name.Trim();
        if (req.Phone != null) user.Phone = req.Phone.Trim();
        if (req.Avatar != null) user.Avatar = req.Avatar;
        if (req.PreferredLanguage != null && (req.PreferredLanguage == "en" || req.PreferredLanguage == "ar"))
            user.PreferredLanguage = req.PreferredLanguage;

        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<UserResponse> { Data = ToDto(user), Message = "Profile updated" });
    }

    /// <summary>Update user's preferred language for push notifications.</summary>
    [HttpPatch("me/language")]
    public async Task<IActionResult> UpdateLanguage([FromBody] UpdateLanguageRequest req)
    {
        if (req.Language != "en" && req.Language != "ar")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Language must be 'en' or 'ar'" });

        var user = await _db.Users.FindAsync(UserId);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        user.PreferredLanguage = req.Language;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Data = new { language = user.PreferredLanguage }, Message = "Language updated" });
    }

    [HttpPatch("me/password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var user = await _db.Users.FindAsync(UserId);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        if (!BCrypt.Net.BCrypt.Verify(req.CurrentPassword, user.PasswordHash))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Current password is incorrect" });

        if (req.NewPassword.Length < 8)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "New password must be at least 8 characters" });

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.NewPassword);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Message = "Password changed successfully" });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? role = null,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null)
    {
        if (UserRole != "super_admin")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Admin only" });

        var query = _db.Users.AsQueryable();

        if (!string.IsNullOrEmpty(role))
            query = query.Where(u => u.Role == role);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(u => u.Status == status);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(u => EF.Functions.Like(u.Name, $"%{search}%")
                                  || EF.Functions.Like(u.Email, $"%{search}%"));

        var total = await query.CountAsync();
        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        return Ok(new ApiResponse<List<UserResponse>>
        {
            Data = users.Select(ToDto).ToList(),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        // super_admin can create any role; venue_owner can only create venue_staff
        if (UserRole == "venue_owner")
        {
            if (req.Role != "venue_staff")
                return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Venue owners can only create venue_staff accounts" });
        }
        else if (UserRole != "super_admin")
        {
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Forbidden" });
        }

        if (await _db.Users.AnyAsync(u => u.Email == req.Email))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Email already in use" });

        var user = new Models.User
        {
            Name        = req.Name.Trim(),
            Email       = req.Email.Trim().ToLower(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Phone       = req.Phone?.Trim(),
            Role        = req.Role,
            Status      = "active",
            Permissions = req.Role == "venue_staff" ? (req.Permissions ?? "read") : null,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<UserResponse> { Data = ToDto(user), Message = "User created" });
    }

    [HttpPatch("{userId}/status")]
    public async Task<IActionResult> UpdateStatus(string userId, [FromBody] StatusUpdateRequest req)
    {
        if (UserRole != "super_admin")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Admin only" });

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        user.Status = req.Status;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<UserResponse> { Data = ToDto(user), Message = "User status updated" });
    }

    [HttpPatch("{userId}/role")]
    public async Task<IActionResult> UpdateRole(string userId, [FromBody] RoleUpdateRequest req)
    {
        if (UserRole != "super_admin")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Admin only" });

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        user.Role = req.Role;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<UserResponse> { Data = ToDto(user), Message = "User role updated" });
    }

    [HttpPatch("{userId}/avatar")]
    public async Task<IActionResult> UpdateAvatar(string userId, [FromBody] AvatarUpdateRequest req)
    {
        if (UserRole != "super_admin")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Admin only" });

        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "User not found" });

        user.Avatar = req.Avatar;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<UserResponse> { Data = ToDto(user), Message = "Avatar updated" });
    }
}
