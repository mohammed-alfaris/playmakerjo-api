using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;

namespace SportsVenueApi.Tests.Infrastructure;

/// <summary>
/// Factories for throwaway per-test entities — booking tests create their own
/// venues so they never mutate (or collide on) the shared fixture seed.
/// </summary>
public static class TestEntities
{
    public static Dictionary<string, object> DailyHours(string open = "08:00", string close = "23:00") => new()
    {
        { "sunday",    new { open, close } },
        { "monday",    new { open, close } },
        { "tuesday",   new { open, close } },
        { "wednesday", new { open, close } },
        { "thursday",  new { open, close } },
        { "friday",    new { open, close } },
        { "saturday",  new { open, close } }
    };

    /// <summary>Non-subdividable single-sport venue (PricePerHour 20, deposit 20%).</summary>
    public static async Task<Venue> CreateBasketballVenue(this DatabaseFixture fx, string ownerId, Action<Venue>? mutate = null)
    {
        var venue = new Venue
        {
            Name = "Throwaway Venue", OwnerId = ownerId,
            Sports = ["basketball"], City = "Amman", Address = "Throwaway Street",
            PricePerHour = 20, DepositPercentage = 20, Status = "active",
            CliqAlias = "throwaway@cliq", OperatingHours = DailyHours()
        };
        mutate?.Invoke(venue);
        return await fx.Insert(venue);
    }

    /// <summary>Football venue with one subdividable 11-aside pitch (half 8, quarter 6) — same shape as test-venue-sub.</summary>
    public static async Task<(Venue Venue, string PitchId)> CreateSubdividableVenue(
        this DatabaseFixture fx, string ownerId, Action<Venue>? mutate = null)
    {
        var pitchId = "p-" + Guid.NewGuid().ToString("N")[..8];
        var venue = new Venue
        {
            Name = "Throwaway Sub Venue", OwnerId = ownerId,
            Sports = ["football"], City = "Amman", Address = "Throwaway Street",
            PricePerHour = 40, DepositPercentage = 20, Status = "active",
            CliqAlias = "throwaway-sub@cliq", OperatingHours = DailyHours(),
            Pitches =
            [
                new PitchDto
                {
                    Id = pitchId, Name = "Main Pitch", Sport = "football",
                    ParentSize = "11", SubSizes = ["8", "6"],
                    SizePrices = new() { ["8"] = 20, ["6"] = 10 },
                    PricePerHour = 40
                }
            ]
        };
        mutate?.Invoke(venue);
        await fx.Insert(venue);
        return (venue, pitchId);
    }

    public static async Task<User> CreatePlayer(this DatabaseFixture fx)
    {
        var id = "u-" + Guid.NewGuid().ToString("N")[..8];
        return await fx.Insert(new User
        {
            Id = id, Name = "Throwaway Player", Email = $"{id}@test.local",
            Phone = "+962790001000", PasswordHash = "never-logs-in",
            Role = "player", Status = "active"
        });
    }

    public static async Task<T> Insert<T>(this DatabaseFixture fx, T entity) where T : class
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    public static async Task<Booking?> LoadBooking(this DatabaseFixture fx, string id)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id);
    }

    public static async Task<Venue?> LoadVenue(this DatabaseFixture fx, string id)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Venues.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id);
    }

    public static async Task<List<Booking>> LoadVenueBookings(this DatabaseFixture fx, string venueId)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().Where(b => b.VenueId == venueId).ToListAsync();
    }

    /// <summary>The ledger rows for one booking, oldest first — the order money arrived in.</summary>
    public static async Task<List<Payment>> LoadPayments(this DatabaseFixture fx, string bookingId)
    {
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Payments.AsNoTracking()
            .Where(p => p.BookingId == bookingId)
            .OrderBy(p => p.Date).ThenBy(p => p.Id)
            .ToListAsync();
    }
}
