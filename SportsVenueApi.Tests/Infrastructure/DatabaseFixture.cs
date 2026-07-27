using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;

namespace SportsVenueApi.Tests.Infrastructure;

/// <summary>
/// Collection fixture for API integration tests. Drops and recreates the test
/// database, boots the API (startup applies migrations), then seeds a fixed set
/// of users and venues that the tests reference by id.
/// </summary>
public class DatabaseFixture : IAsyncLifetime
{
    public TestWebApplicationFactory Factory { get; private set; } = null!;

    public const string TestPassword = "Test#12345";

    public string AdminId => "test-admin";
    public string OwnerAId => "test-owner-a";
    public string OwnerBId => "test-owner-b";
    /// <summary>venue_owner who owns no venues.</summary>
    public string OwnerCId => "test-owner-c";
    public string PlayerId => "test-player";

    /// <summary>venue_staff working for Owner A with "write" — the counter clerk.</summary>
    public string StaffAWriteId => "test-staff-a-write";
    /// <summary>venue_staff working for Owner A with "read" — may look, may not touch.</summary>
    public string StaffAReadId => "test-staff-a-read";
    /// <summary>venue_staff working for Owner B — used to prove staff cannot cross owners.</summary>
    public string StaffBWriteId => "test-staff-b-write";
    /// <summary>
    /// venue_staff with NO owner link, as legacy rows created before staff→owner linking
    /// existed will look. Must resolve to no access anywhere.
    /// </summary>
    public string StaffUnlinkedId => "test-staff-unlinked";

    public string VenueAId => "test-venue-a";
    public string VenueBId => "test-venue-b";
    /// <summary>Football venue with a subdividable 11-aside pitch (half 8, quarter 6).</summary>
    public string VenueSubId => "test-venue-sub";

    public async Task InitializeAsync()
    {
        await using (var conn = new MySqlConnection(TestDb.ServerConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"DROP DATABASE IF EXISTS `{TestDb.DatabaseName}`; " +
                $"CREATE DATABASE `{TestDb.DatabaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";
            await cmd.ExecuteNonQueryAsync();
        }

        Factory = new TestWebApplicationFactory();
        _ = Factory.Services; // boot the host — startup runs MigrateAsync against the fresh DB

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await Seed(db);
    }

    public Task DisposeAsync()
    {
        Factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task Seed(AppDbContext db)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(TestPassword);

        db.Users.AddRange(
            new User { Id = AdminId, Name = "Test Admin", Email = "admin@test.local", Phone = "+962790000001", PasswordHash = hash, Role = "super_admin", Status = "active" },
            new User { Id = OwnerAId, Name = "Test Owner A", Email = "owner-a@test.local", Phone = "+962790000002", PasswordHash = hash, Role = "venue_owner", Status = "active" },
            new User { Id = OwnerBId, Name = "Test Owner B", Email = "owner-b@test.local", Phone = "+962790000003", PasswordHash = hash, Role = "venue_owner", Status = "active" },
            new User { Id = OwnerCId, Name = "Test Owner C", Email = "owner-c@test.local", Phone = "+962790000004", PasswordHash = hash, Role = "venue_owner", Status = "active" },
            new User { Id = PlayerId, Name = "Test Player", Email = "player@test.local", Phone = "+962790000005", PasswordHash = hash, Role = "player", Status = "active" },
            new User { Id = StaffAWriteId, Name = "Staff A Write", Email = "staff-a-write@test.local", Phone = "+962790000006", PasswordHash = hash, Role = "venue_staff", Status = "active", Permissions = "write", ManagedByOwnerId = OwnerAId },
            new User { Id = StaffAReadId, Name = "Staff A Read", Email = "staff-a-read@test.local", Phone = "+962790000007", PasswordHash = hash, Role = "venue_staff", Status = "active", Permissions = "read", ManagedByOwnerId = OwnerAId },
            new User { Id = StaffBWriteId, Name = "Staff B Write", Email = "staff-b-write@test.local", Phone = "+962790000008", PasswordHash = hash, Role = "venue_staff", Status = "active", Permissions = "write", ManagedByOwnerId = OwnerBId },
            new User { Id = StaffUnlinkedId, Name = "Staff Unlinked", Email = "staff-unlinked@test.local", Phone = "+962790000009", PasswordHash = hash, Role = "venue_staff", Status = "active", Permissions = "write", ManagedByOwnerId = null });
        await db.SaveChangesAsync();

        // Daily 08:00–23:00 — full day-name keys, matching the dashboard editor and SeedData.
        var hours = new Dictionary<string, object>
        {
            { "sunday",    new { open = "08:00", close = "23:00" } },
            { "monday",    new { open = "08:00", close = "23:00" } },
            { "tuesday",   new { open = "08:00", close = "23:00" } },
            { "wednesday", new { open = "08:00", close = "23:00" } },
            { "thursday",  new { open = "08:00", close = "23:00" } },
            { "friday",    new { open = "08:00", close = "23:00" } },
            { "saturday",  new { open = "08:00", close = "23:00" } }
        };

        var venueA = new Venue
        {
            Id = VenueAId, Name = "Test Venue A", OwnerId = OwnerAId,
            Sports = ["basketball"], City = "Amman", Address = "Test Street 1",
            PricePerHour = 20, DepositPercentage = 20, Status = "active",
            CliqAlias = "venue-a@cliq", OperatingHours = hours
        };

        var venueB = new Venue
        {
            Id = VenueBId, Name = "Test Venue B", OwnerId = OwnerBId,
            Sports = ["basketball"], City = "Irbid", Address = "Test Street 2",
            PricePerHour = 20, DepositPercentage = 20, Status = "active",
            CliqAlias = "venue-b@cliq", OperatingHours = hours
        };

        // Subdividable football venue: 11-aside parent split as half=8 / quarter=6,
        // with a size_prices entry per sub-size (the parent uses pricePerHour).
        var venueSub = new Venue
        {
            Id = VenueSubId, Name = "Test Venue Sub", OwnerId = OwnerAId,
            Sports = ["football"], City = "Amman", Address = "Test Street 3",
            PricePerHour = 40, DepositPercentage = 20, Status = "active",
            CliqAlias = "venue-sub@cliq", OperatingHours = hours,
            Pitches =
            [
                new PitchDto
                {
                    Id = "test-pitch-sub", Name = "Main Pitch", Sport = "football",
                    ParentSize = "11", SubSizes = ["8", "6"],
                    SizePrices = new() { ["8"] = 20, ["6"] = 10 },
                    PricePerHour = 40
                }
            ]
        };

        db.Venues.AddRange(venueA, venueB, venueSub);
        await db.SaveChangesAsync();
    }
}
