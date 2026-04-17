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
    private readonly ILogger<UploadsController> _logger;

    private static readonly HashSet<string> ValidCategories = new() { "venue", "avatar", "proof" };

    // Map MIME content types to file extensions so files picked from a content
    // provider (which sometimes arrive with no filename extension) still land
    // on disk with the right suffix.
    private static readonly Dictionary<string, string> ContentTypeToExt = new()
    {
        { "image/jpeg", ".jpg" },
        { "image/jpg",  ".jpg" },
        { "image/png",  ".png" },
        { "image/webp", ".webp" },
        { "image/heic", ".heic" },
        { "image/heif", ".heif" },
    };

    public UploadsController(IConfiguration config, IWebHostEnvironment env, ILogger<UploadsController> logger)
    {
        _config = config;
        _env = env;
        _logger = logger;
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
        {
            _logger.LogWarning("Upload 400: no file (category={Category})", category);
            return BadRequest(new ApiResponse<object> { Success = false, Message = "No file provided." });
        }

        // Validate category
        category = category?.ToLowerInvariant() ?? "";
        if (!ValidCategories.Contains(category))
        {
            _logger.LogWarning("Upload 400: invalid category '{Category}' (filename={FileName})", category, file.FileName);
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Invalid category. Must be one of: {string.Join(", ", ValidCategories)}" });
        }

        // Validate file size
        var maxSizeMB = _config.GetValue<int>("Uploads:MaxFileSizeMB", 5);
        if (file.Length > maxSizeMB * 1024 * 1024)
        {
            _logger.LogWarning("Upload 400: too large ({Size} bytes > {Max}MB, filename={FileName})", file.Length, maxSizeMB, file.FileName);
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"File too large. Maximum size is {maxSizeMB}MB." });
        }

        // Validate extension — fall back to content-type if the filename has
        // no extension (common when image_picker on Android returns a file
        // whose name is just a UUID with no suffix).
        var allowedExtensions = _config.GetSection("Uploads:AllowedExtensions").Get<string[]>()
            ?? new[] { ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif" };
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            var ct = (file.ContentType ?? "").ToLowerInvariant();
            if (ContentTypeToExt.TryGetValue(ct, out var mapped))
            {
                ext = mapped;
                _logger.LogInformation("Upload: derived ext '{Ext}' from content-type '{ContentType}'", ext, ct);
            }
        }
        if (!allowedExtensions.Contains(ext))
        {
            _logger.LogWarning("Upload 400: invalid extension '{Ext}' (filename={FileName}, contentType={ContentType})", ext, file.FileName, file.ContentType);
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Invalid file type '{(ext == "" ? "(none)" : ext)}'. Allowed: {string.Join(", ", allowedExtensions)}" });
        }

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
