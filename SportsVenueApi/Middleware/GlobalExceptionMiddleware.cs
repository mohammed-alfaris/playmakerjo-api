using System.Text.Json;
using SportsVenueApi.DTOs;

namespace SportsVenueApi.Middleware;

public class GlobalExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionMiddleware> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (UnauthorizedAccessException ex)
        {
            await WriteAsync(context, StatusCodes.Status401Unauthorized, ex.Message);
        }
        catch (KeyNotFoundException ex)
        {
            await WriteAsync(context, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);
            var message = _env.IsDevelopment()
                ? $"{ex.GetType().Name}: {ex.Message}"
                : "An unexpected error occurred.";
            await WriteAsync(context, StatusCodes.Status500InternalServerError, message);
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var payload = new ApiResponse<object?>
        {
            Success = false,
            Data = null,
            Message = message
        };
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
    }
}
