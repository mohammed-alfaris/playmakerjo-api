using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MySqlConnector;

namespace SportsVenueApi.Tests.Infrastructure;

/// <summary>
/// Test database connection strings: TEST_MYSQL_CONNECTION env var when set (CI),
/// otherwise the local dev connection from the API's (untracked)
/// appsettings.Development.json, retargeted at a dedicated sportsvenue_test database
/// so no credentials live in the repo.
/// </summary>
public static class TestDb
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_MYSQL_CONNECTION") ?? FromDevAppSettings();

    private static string FromDevAppSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "SportsVenueApi", "appsettings.Development.json");
        if (!File.Exists(path))
            throw new InvalidOperationException(
                "Set TEST_MYSQL_CONNECTION or provide SportsVenueApi/appsettings.Development.json " +
                "with ConnectionStrings:DefaultConnection — tests need a real MySQL server.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var dev = doc.RootElement.GetProperty("ConnectionStrings")
                                 .GetProperty("DefaultConnection").GetString()!;
        return new MySqlConnectionStringBuilder(dev) { Database = "sportsvenue_test" }.ConnectionString;
    }

    public static string DatabaseName =>
        new MySqlConnectionStringBuilder(ConnectionString).Database;

    /// <summary>Same server/credentials without a database — used for the DROP/CREATE step.</summary>
    public static string ServerConnectionString
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder(ConnectionString) { Database = "" };
            return builder.ConnectionString;
        }
    }
}

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection", TestDb.ConnectionString);
        // Skip the real Firebase credential file — a second FirebaseApp.Create(options)
        // would throw when another factory in the same process already created the
        // default app. The empty value falls through to the try/catch-wrapped branch.
        builder.UseSetting("Firebase:CredentialFile", "");
        // High limits so the per-user uploads/booking-create limiters never
        // reject regular suite traffic. Auth is also raised — the suite drives
        // many login/register/refresh calls from one loopback IP within a minute,
        // which would otherwise trip the per-IP 5/min auth limiter.
        builder.UseSetting("RateLimiting:Uploads:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:BookingCreate:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "100000");
    }
}
