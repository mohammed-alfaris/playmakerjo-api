using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsVenueApi.DTOs;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/uploads")]
[Authorize]
public class UploadsController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;

    private static readonly HashSet<string> ValidCategories = new() { "venue", "avatar", "proof" };

    public UploadsController(IConfiguration config, IWebHostEnvironment env)
    {
        _config = config;
        _env = env;
    }

    /// <summary>
    /// Upload a file. Returns the public URL.
    /// POST /api/v1/uploads  (multipart/form-data: file + category)
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(5 * 1024 * 1024)] // 5MB
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] string category)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "No file provided." });

        // Validate category
        category = category?.ToLowerInvariant() ?? "";
        if (!ValidCategories.Contains(category))
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Invalid category. Must be one of: {string.Join(", ", ValidCategories)}" });

        // Validate file size
        var maxSizeMB = _config.GetValue<int>("Uploads:MaxFileSizeMB", 5);
        if (file.Length > maxSizeMB * 1024 * 1024)
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"File too large. Maximum size is {maxSizeMB}MB." });

        // Validate extension
        var allowedExtensions = _config.GetSection("Uploads:AllowedExtensions").Get<string[]>()
            ?? new[] { ".jpg", ".jpeg", ".png", ".webp" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(ext))
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Invalid file type. Allowed: {string.Join(", ", allowedExtensions)}" });

        // Map category to folder
        var folderName = category switch
        {
            "venue" => "venues",
            "avatar" => "avatars",
            "proof" => "proofs",
            _ => "misc"
        };

        // Generate unique filename
        var fileName = $"{Guid.NewGuid()}{ext}";
        var relativePath = Path.Combine("uploads", folderName, fileName);
        var absolutePath = Path.Combine(_env.ContentRootPath, "wwwroot", relativePath);

        // Ensure directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        // Save file
        using (var stream = new FileStream(absolutePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }

        // Build public URL
        var baseUrl = _config["Uploads:BaseUrl"]?.TrimEnd('/') ?? $"{Request.Scheme}://{Request.Host}";
        var url = $"{baseUrl}/{relativePath.Replace('\\', '/')}";

        return Ok(new ApiResponse<object>
        {
            Data = new { url },
            Message = "File uploaded successfully."
        });
    }
}
