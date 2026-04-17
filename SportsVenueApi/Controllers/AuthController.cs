using Google.Apis.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Auth;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly JwtService _jwt;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly string _uploadsBaseUrl;

    public AuthController(AppDbContext db, JwtService jwt, IWebHostEnvironment env, IConfiguration config, ILogger<AuthController> logger)
    {
        _db = db;
        _jwt = jwt;
        _env = env;
        _config = config;
        _logger = logger;
        _uploadsBaseUrl = config["Uploads:BaseUrl"]?.TrimEnd('/') ?? "";
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email);
        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "Invalid credentials" });

        if (user.Status == "banned")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Account is banned" });

        return Ok(IssueTokens(user));
    }

    // POST /api/v1/auth/google
    // Validates a Google ID token (issued by GoogleSignIn on the device)
    // and logs the matching user in. If no user exists with that email we
    // return 404 so the client can prompt them to register first — per the
    // product decision to gate first-time login behind phone-number capture.
    [HttpPost("google")]
    public async Task<IActionResult> GoogleSignIn([FromBody] GoogleSignInRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.IdToken))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "idToken is required" });

        var expectedAudience = _config["Google:WebClientId"];
        if (string.IsNullOrWhiteSpace(expectedAudience))
        {
            _logger.LogError("Google sign-in hit but Google:WebClientId is not configured");
            return StatusCode(500, new ApiResponse<object> { Success = false, Message = "Google sign-in not configured on this server" });
        }

        GoogleJsonWebSignature.Payload payload;
        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { expectedAudience },
            };
            payload = await GoogleJsonWebSignature.ValidateAsync(req.IdToken, settings);
        }
        catch (InvalidJwtException ex)
        {
            _logger.LogWarning(ex, "Google ID token validation failed");
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "Invalid Google token" });
        }

        var email = (payload.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Google account has no email" });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Message = "No account found for this Google email. Please register first with your phone number."
            });

        if (user.Status == "banned")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Account is banned" });

        return Ok(IssueTokens(user));
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Name is required" });

        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Email is required" });

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Password must be at least 8 characters" });

        var exists = await _db.Users.AnyAsync(u => u.Email == req.Email);
        if (exists)
            return Conflict(new ApiResponse<object> { Success = false, Message = "Email already exists" });

        var user = new User
        {
            Name = req.Name.Trim(),
            Email = req.Email.Trim().ToLower(),
            Phone = req.Phone?.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Role = "player",
            Status = "active"
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(IssueTokens(user));
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var token = Request.Cookies["refresh_token"];
        if (string.IsNullOrEmpty(token))
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "No refresh token" });

        var principal = _jwt.ValidateToken(token, "refresh");
        if (principal == null)
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "Invalid refresh token" });

        var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                  ?? principal.FindFirst("sub")?.Value;
        if (userId == null)
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "Invalid token" });

        var user = await _db.Users.FindAsync(userId);
        if (user == null || user.Status == "banned")
            return Unauthorized(new ApiResponse<object> { Success = false, Message = "User not found or banned" });

        var accessToken = _jwt.CreateAccessToken(user.Id, user.Role);

        return Ok(new ApiResponse<TokenData>
        {
            Data = new TokenData { AccessToken = accessToken },
            Message = "Token refreshed"
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("refresh_token", new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = !_env.IsDevelopment(),
            SameSite = _env.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict,
        });
        return Ok(new ApiResponse<object> { Data = null, Message = "Logged out" });
    }

    private ApiResponse<LoginData> IssueTokens(User user)
    {
        var accessToken = _jwt.CreateAccessToken(user.Id, user.Role);
        var refreshToken = _jwt.CreateRefreshToken(user.Id, user.Role);

        Response.Cookies.Append("refresh_token", refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = !_env.IsDevelopment(),
            SameSite = _env.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(7)
        });

        return new ApiResponse<LoginData>
        {
            Data = new LoginData
            {
                User = new AuthUserResponse
                {
                    Id = user.Id,
                    Name = user.Name,
                    Email = user.Email,
                    Role = user.Role,
                    Phone = user.Phone,
                    Avatar = UploadUrlHelper.Normalize(user.Avatar, _uploadsBaseUrl)
                },
                AccessToken = accessToken
            },
            Message = "Login successful"
        };
    }
}
