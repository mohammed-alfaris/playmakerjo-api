using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Auth;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Helpers;
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
        Avatar = UploadUrlHelper.Normalize(u.Avatar, _uploadsBaseUrl),
        Permissions = u.Permissions,
        ManagedByOwnerId = u.ManagedByOwnerId,
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

        // The phone is an identity key, not a display field: an app booking resolves
        // the venue's customer record from it. Written raw and unchecked, anyone could
        // type a regular's number, book a slot, and have their bookings — and their
        // no-shows — merge into that person's history at the venue.
        //
        // Normalising here means the value is compared in the same canonical form the
        // customer book uses, so "0791234567" and "+962791234567" cannot be two people.
        if (req.Phone != null && req.Phone.Trim() != user.Phone)
        {
            // Only validated when it actually changes: the dashboard PATCHes the whole
            // profile back, and a user whose stored number predates this rule must still
            // be able to edit their name.
            var normalized = PhoneNormalizer.ToE164Jo(req.Phone);
            if (normalized == null)
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "Enter a valid Jordanian mobile number."
                });

            var takenByAnother = await _db.Users
                .AnyAsync(u => u.Id != user.Id && u.Phone == normalized);
            if (takenByAnother)
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "That mobile number is already in use."
                });

            user.Phone = normalized;
        }
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

    /// <summary>
    /// An owner's own staff. Separate from <see cref="List"/>, which is the platform-wide
    /// user directory and stays admin-only — an owner must never be handed a list that
    /// includes players, other owners, or a role dropdown. Until this existed an owner
    /// could create staff via POST /users and then never see them again.
    /// </summary>
    [HttpGet("staff")]
    public async Task<IActionResult> ListStaff(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? owner_id = null)
    {
        string ownerId;
        if (UserRole == "venue_owner")
        {
            // The query string is ignored — an owner only ever sees their own team.
            ownerId = UserId;
        }
        else if (UserRole == "super_admin")
        {
            if (string.IsNullOrWhiteSpace(owner_id))
                return BadRequest(new ApiResponse<object> { Success = false, Message = "owner_id is required" });
            ownerId = owner_id;
        }
        else
        {
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Forbidden" });
        }

        var query = _db.Users.Where(u => u.Role == "venue_staff" && u.ManagedByOwnerId == ownerId);

        var total = await query.CountAsync();
        var staff = await query
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        return Ok(new ApiResponse<List<UserResponse>>
        {
            Data = staff.Select(ToDto).ToList(),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    /// <summary>Change a staff member's read/write level. Owners may only touch their own.</summary>
    [HttpPatch("{userId}/permissions")]
    public async Task<IActionResult> UpdatePermissions(string userId, [FromBody] PermissionsUpdateRequest req)
    {
        if (req.Permissions is not ("read" or "write"))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "permissions must be 'read' or 'write'" });

        var user = await _db.Users.FindAsync(userId);
        if (user == null || user.Role != "venue_staff")
            return NotFound(new ApiResponse<object> { Success = false, Message = "Staff account not found" });

        var allowed = UserRole == "super_admin"
                   || (UserRole == "venue_owner" && user.ManagedByOwnerId == UserId);
        if (!allowed)
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Forbidden" });

        user.Permissions = req.Permissions;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<UserResponse> { Data = ToDto(user), Message = "Permissions updated" });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
    {
        // super_admin can create any role; venue_owner can only create venue_staff
        if (UserRole == "venue_owner")
        {
            if (req.Role != "venue_staff")
                return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Venue owners can only create venue_staff accounts" });

            if (!await _db.Venues.AnyAsync(v => v.OwnerId == UserId))
                return StatusCode(403, new ApiResponse<object> { Success = false, Message = "You must own at least one venue to create staff accounts" });
        }
        else if (UserRole != "super_admin")
        {
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Forbidden" });
        }

        if (await _db.Users.AnyAsync(u => u.Email == req.Email))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Email already in use" });

        // Which owner does this staff member work for? An owner creating staff always gets
        // themselves; an admin must name the owner explicitly, because a staff account with
        // no owner can reach nothing and would look like a silent failure.
        string? managedByOwnerId = null;
        if (req.Role == "venue_staff")
        {
            if (UserRole == "venue_owner")
            {
                managedByOwnerId = UserId;
            }
            else
            {
                if (string.IsNullOrWhiteSpace(req.ManagedByOwnerId))
                    return BadRequest(new ApiResponse<object> { Success = false, Message = "managedByOwnerId is required when creating a staff account" });

                var owner = await _db.Users.FirstOrDefaultAsync(u => u.Id == req.ManagedByOwnerId);
                if (owner == null || owner.Role != "venue_owner")
                    return BadRequest(new ApiResponse<object> { Success = false, Message = "managedByOwnerId must reference an existing venue_owner" });

                managedByOwnerId = owner.Id;
            }
        }

        var user = new Models.User
        {
            Name        = req.Name.Trim(),
            Email       = req.Email.Trim().ToLower(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Phone       = req.Phone?.Trim(),
            Role        = req.Role,
            Status      = "active",
            Permissions = req.Role == "venue_staff" ? (req.Permissions ?? "read") : null,
            ManagedByOwnerId = managedByOwnerId,
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
