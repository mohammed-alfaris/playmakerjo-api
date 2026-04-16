using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.RateLimiting;
using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SportsVenueApi.Data;
using SportsVenueApi.Services;

// Note: ASP.NET maps JWT "sub" -> ClaimTypes.NameIdentifier, "role" -> ClaimTypes.Role

var builder = WebApplication.CreateBuilder(args);

// Serilog
builder.Host.UseSerilog((context, config) =>
{
    config.ReadFrom.Configuration(context.Configuration)
          .Enrich.FromLogContext()
          .WriteTo.Console();
});

// Database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is not configured. " +
        "Set it via appsettings.Development.json (dev), user-secrets, or the " +
        "ConnectionStrings__DefaultConnection environment variable (prod).");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// JWT
var secretKey = builder.Configuration["Jwt:SecretKey"];
if (string.IsNullOrWhiteSpace(secretKey) || secretKey.Length < 32)
    throw new InvalidOperationException(
        "Jwt:SecretKey is missing or shorter than 32 characters. " +
        "Set it via user-secrets (dev) or the Jwt__SecretKey environment variable (prod).");

// Refuse the well-known placeholder secret in production. In development we tolerate it
// to keep the local workflow simple, but production must use a real random key.
string[] knownJwtPlaceholders =
{
    "CHANGE-THIS-IN-PRODUCTION-MIN-32-CHARS!!",
    "REPLACE-WITH-STRONG-RANDOM-KEY-MIN-32-CHARS"
};
if (builder.Environment.IsProduction() && knownJwtPlaceholders.Contains(secretKey))
    throw new InvalidOperationException(
        "Jwt:SecretKey is set to a well-known placeholder value. " +
        "Generate a random key (e.g. `openssl rand -base64 48`) and set it via " +
        "the Jwt__SecretKey environment variable.");
builder.Services.AddSingleton<JwtService>();
builder.Services.AddScoped<NotificationService>();

// Firebase Admin SDK
var firebaseCredPath = builder.Configuration["Firebase:CredentialFile"];
if (!string.IsNullOrEmpty(firebaseCredPath) && File.Exists(firebaseCredPath))
{
    FirebaseApp.Create(new AppOptions
    {
        Credential = GoogleCredential.FromFile(firebaseCredPath),
    });
}
else
{
    // Try default credentials (GOOGLE_APPLICATION_CREDENTIALS env var)
    try { FirebaseApp.Create(); }
    catch { Log.Warning("Firebase Admin SDK not initialized — push notifications disabled. Set Firebase:CredentialFile in appsettings.json"); }
}
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ValidateIssuer = true,
            ValidIssuer = "YallaNhjez",
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

// CORS
var corsOrigins = builder.Configuration["Cors:Origins"] ?? "http://localhost:5173";
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        // AllowAnyOrigin() is incompatible with AllowCredentials() (cookies).
        // Always use explicit origins so withCredentials works in the browser.
        var origins = builder.Environment.IsDevelopment()
            ? new[] { "http://localhost:5173", "http://localhost:5174", "http://localhost:5175",
                       "http://localhost:5176", "http://localhost:5177",
                       "http://localhost:8080", "http://localhost:8081", "http://localhost:8082",
                       "http://localhost:3000" }
            : corsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        policy.WithOrigins(origins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 5;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

// Health checks
builder.Services.AddHealthChecks()
    .AddMySql(connectionString, name: "mysql");

// Configure multipart body size for file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 5 * 1024 * 1024; // 5MB
});

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Seed command: dotnet run -- --seed
if (args.Contains("--seed"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await SeedData.Initialize(db);
    return;
}

// Auto-apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    // Ensure UTF-8 support for Arabic text
    try
    {
        await db.Database.ExecuteSqlRawAsync("SET NAMES utf8mb4");
        await db.Database.ExecuteSqlRawAsync(
            "ALTER DATABASE sportsvenue CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "UTF-8 charset migration warning (non-fatal)");
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseMiddleware<SportsVenueApi.Middleware.GlobalExceptionMiddleware>();

app.UseSerilogRequestLogging();
app.UseCors();

// Serve uploaded files as static files
var uploadsPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
Directory.CreateDirectory(Path.Combine(uploadsPath, "uploads", "venues"));
Directory.CreateDirectory(Path.Combine(uploadsPath, "uploads", "avatars"));
Directory.CreateDirectory(Path.Combine(uploadsPath, "uploads", "proofs"));
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
