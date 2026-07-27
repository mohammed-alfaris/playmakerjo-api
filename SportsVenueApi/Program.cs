using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using FirebaseAdmin;
using FluentValidation;
using FluentValidation.AspNetCore;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using SportsVenueApi.Data;
using SportsVenueApi.Helpers;
using SportsVenueApi.Jobs;
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
builder.Services.AddScoped<SettingsService>();

// ── Unpaid-booking expiry ───────────────────────────────────────────────────────────
builder.Services.Configure<BookingExpiryOptions>(
    builder.Configuration.GetSection(BookingExpiryOptions.Section));

var expiryOptions = builder.Configuration
    .GetSection(BookingExpiryOptions.Section).Get<BookingExpiryOptions>() ?? new BookingExpiryOptions();

// The window is needed at CREATION time to stamp a deadline, which happens whether or not
// the sweeper runs. Registered separately from the hosted service for that reason: turning
// the job off must not silently change what gets written on new bookings.
builder.Services.AddSingleton(new ExpiryPolicy(
    expiryOptions.WindowMinutes, expiryOptions.SlotBufferMinutes, expiryOptions.MinimumMinutes));

builder.Services.AddScoped<UnpaidBookingSweep>();

if (expiryOptions.Enabled)
{
    builder.Services.AddHostedService<BookingExpiryService>();

    // Second layer of protection for the API process. The job's own loop already catches
    // everything, but the .NET default here is StopHost — one escaped exception in any
    // hosted service tears down the whole API, and with `restart: unless-stopped` that
    // becomes an invisible crash-restart loop instead of a visible outage.
    builder.Services.Configure<HostOptions>(o =>
        o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
}
// When disabled it is not registered at all, rather than registered-and-idle. This is the
// first background WRITER in the process and what it writes is customers' bookings; it
// should never start because someone pulled main, ran the test suite, or booted a laptop.
//
// That matters more than it looks: AuthControllerTests boots a BARE
// WebApplicationFactory<Program> (AuthControllerTests.cs:7), which never receives the
// test-database override — so a hosted service running there would sweep the developer's
// real dev database.
//
// No log line here on purpose: Serilog's static Log.* is not wired up until builder.Build()
// below, so anything written at this point is silently discarded. The job announces itself
// from inside ExecuteAsync using an injected ILogger, which does work.

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
            ValidIssuer = "PlayMakerJO",
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // Access and refresh tokens are signed with the same key and issuer and carry
        // the same sub/role claims — they differ ONLY by the "type" claim that
        // JwtService stamps. Without this check a refresh token authenticates against
        // every endpoint, silently turning the deliberate 15-minute access window into
        // a 7-day one and defeating the ban check that only runs on /auth/refresh.
        // JwtService.CreateToken always sets this claim, so failing closed is safe.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                var tokenType = context.Principal?.FindFirst("type")?.Value;
                if (tokenType != "access")
                    context.Fail("Only access tokens are accepted on this endpoint.");
                return Task.CompletedTask;
            }
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

// Rate limiting — "auth" partitions per client IP so one attacker can't exhaust
// logins for everyone; "uploads" and "booking-create" partition per authenticated user.
static string UserOrIpKey(HttpContext context) =>
    context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    var uploadsLimit = builder.Configuration.GetValue("RateLimiting:Uploads:PermitLimit", 20);
    options.AddPolicy("uploads", context =>
        RateLimitPartition.GetFixedWindowLimiter(UserOrIpKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = uploadsLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    var bookingCreateLimit = builder.Configuration.GetValue("RateLimiting:BookingCreate:PermitLimit", 10);
    options.AddPolicy("booking-create", context =>
        RateLimitPartition.GetFixedWindowLimiter(UserOrIpKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = bookingCreateLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// Health checks
builder.Services.AddHealthChecks()
    .AddMySql(connectionString, name: "mysql");

// Configure multipart body size for file uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 5 * 1024 * 1024; // 5MB
});

builder.Services.AddValidatorsFromAssemblyContaining<SportsVenueApi.Validation.VenueCreateRequestValidator>();
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Seed command: dotnet run -- --seed
//
// DESTRUCTIVE. SeedData.Initialize begins with EnsureDeletedAsync() — it drops the
// entire database and recreates it with demo data. On an environment holding real
// customers that is total, unrecoverable data loss from a single command, so it is
// refused outright in Production. Anyone who genuinely needs to reset a production
// database can restore from a backup, which forces the deliberate, reversible path.
if (args.Contains("--seed"))
{
    if (app.Environment.IsProduction())
    {
        Log.Fatal(
            "Refusing to seed: --seed DROPS THE ENTIRE DATABASE and this is a Production "
            + "environment. If you really intend to destroy this data, restore from a backup instead.");
        Environment.ExitCode = 1;
        return;
    }

    Log.Warning(
        "Seeding {Environment}: the existing database is about to be DROPPED and replaced with demo data.",
        app.Environment.EnvironmentName);

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

    // Seed the singleton platform_settings row if missing so GET /platform/status
    // responds correctly even before any admin has opened the Settings page.
    try
    {
        if (!await db.PlatformSettings.AnyAsync(s => s.Id == 1))
        {
            db.PlatformSettings.Add(new SportsVenueApi.Models.PlatformSettings { Id = 1 });
            await db.SaveChangesAsync();
            Log.Information("Seeded default platform_settings row (id=1)");
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "Could not seed platform_settings (non-fatal — table may not exist yet)");
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
// Register HEIC/HEIF MIME types so StaticFileMiddleware serves them instead
// of returning 404. Without this, iOS/Samsung photos uploaded as HEIC come
// back with a not-found error when the dashboard or app tries to render them.
var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".heic"] = "image/heic";
contentTypeProvider.Mappings[".heif"] = "image/heif";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider,
});
// Rate limiter runs after authentication so per-user policies can key on claims.
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

public partial class Program { }
