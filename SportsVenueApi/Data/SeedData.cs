using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Models;

namespace SportsVenueApi.Data;

public static class SeedData
{
    public static async Task Initialize(AppDbContext db)
    {
        // Drop and recreate via migrations
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var adminHash = BCrypt.Net.BCrypt.HashPassword("M7md.272");
        var ownerHash = BCrypt.Net.BCrypt.HashPassword("owner123");
        var playerHash = BCrypt.Net.BCrypt.HashPassword("password123");

        var users = new List<User>
        {
            new() { Id = "u1", Name = "Ahmad Al-Hassan", Email = "amermohammed500@gmail.com", Phone = "+962791000001", PasswordHash = adminHash, Role = "super_admin", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Ahmad", CreatedAt = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u2", Name = "Khalid Al-Natour", Email = "khalid@venues.jo", Phone = "+962791000002", PasswordHash = ownerHash, Role = "venue_owner", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Khalid", CreatedAt = new DateTime(2024, 1, 10, 9, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u3", Name = "Rania Haddad", Email = "rania@venues.jo", Phone = "+962791000003", PasswordHash = ownerHash, Role = "venue_owner", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Rania", CreatedAt = new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u4", Name = "Omar Farouq", Email = "omar.f@venues.jo", Phone = "+962791000004", PasswordHash = ownerHash, Role = "venue_owner", Status = "banned", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Omar", CreatedAt = new DateTime(2024, 1, 20, 11, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u5", Name = "Lina Barakat", Email = "lina.b@venues.jo", Phone = "+962791000005", PasswordHash = ownerHash, Role = "venue_owner", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Lina", CreatedAt = new DateTime(2024, 2, 1, 8, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u6", Name = "Tariq Mansour", Email = "tariq@staff.jo", Phone = "+962791000006", PasswordHash = playerHash, Role = "venue_staff", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Tariq", CreatedAt = new DateTime(2024, 2, 5, 9, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u7", Name = "Dina Saleh", Email = "dina@staff.jo", Phone = "+962791000007", PasswordHash = playerHash, Role = "venue_staff", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Dina", CreatedAt = new DateTime(2024, 2, 10, 10, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u8", Name = "Faisal Al-Zoubi", Email = "faisal.z@player.jo", Phone = "+962791000008", PasswordHash = playerHash, Role = "player", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Faisal", CreatedAt = new DateTime(2024, 2, 15, 11, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u9", Name = "Nour Khalil", Email = "nour.k@player.jo", Phone = "+962791000009", PasswordHash = playerHash, Role = "player", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Nour", CreatedAt = new DateTime(2024, 2, 20, 8, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u10", Name = "Youssef Amawi", Email = "youssef@player.jo", Phone = "+962791000010", PasswordHash = playerHash, Role = "player", Status = "banned", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Youssef", CreatedAt = new DateTime(2024, 3, 1, 9, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u11", Name = "Sara Nimri", Email = "sara.n@player.jo", Phone = "+962791000011", PasswordHash = playerHash, Role = "player", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Sara", CreatedAt = new DateTime(2024, 3, 5, 10, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u12", Name = "Hassan Khatib", Email = "hassan.k@player.jo", Phone = "+962791000012", PasswordHash = playerHash, Role = "player", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Hassan", CreatedAt = new DateTime(2024, 3, 10, 11, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u13", Name = "Maya Shawabkeh", Email = "maya.s@player.jo", Phone = "+962791000013", PasswordHash = playerHash, Role = "player", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Maya", CreatedAt = new DateTime(2024, 3, 15, 8, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u14", Name = "Bilal Otoum", Email = "bilal.o@player.jo", Phone = "+962791000014", PasswordHash = playerHash, Role = "player", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Bilal", CreatedAt = new DateTime(2024, 3, 20, 9, 0, 0, DateTimeKind.Utc) },
            new() { Id = "u15", Name = "Rana Zreiqat", Email = "rana.z@player.jo", Phone = "+962791000015", PasswordHash = playerHash, Role = "player", Status = "active", Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Rana", CreatedAt = new DateTime(2024, 3, 25, 10, 0, 0, DateTimeKind.Utc) },
        };

        db.Users.AddRange(users);
        await db.SaveChangesAsync();

        var venues = new List<Venue>
        {
            new() { Id = "v1", Name = "Al-Ameen Football Arena", OwnerId = "u2", City = "Amman", Address = "Al-Rabweh, Amman", PricePerHour = 25, Status = "active", Description = "Full-size football pitch with floodlights.", Latitude = 31.9819, Longitude = 35.8718, CreatedAt = new DateTime(2024, 2, 12, 8, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v2", Name = "Capital Sports Hub", OwnerId = "u3", City = "Amman", Address = "Sweifieh, Amman", PricePerHour = 30, Status = "active", Description = "Indoor multi-sport center.", Latitude = 31.9560, Longitude = 35.8670, CreatedAt = new DateTime(2024, 2, 20, 9, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v3", Name = "Zarqa Tennis Club", OwnerId = "u4", City = "Zarqa", Address = "New Zarqa, Zarqa", PricePerHour = 20, Status = "inactive", Description = "Clay courts, 4 outdoor tennis courts.", Latitude = 32.0637, Longitude = 36.1036, CreatedAt = new DateTime(2024, 3, 5, 10, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v4", Name = "Northern Star Padel", OwnerId = "u5", City = "Irbid", Address = "University Street, Irbid", PricePerHour = 18, Status = "active", Description = "3 padel courts, air conditioned.", Latitude = 32.5568, Longitude = 35.8469, CreatedAt = new DateTime(2024, 3, 25, 11, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v5", Name = "Aqaba Beach Sports", OwnerId = "u2", City = "Aqaba", Address = "South Beach, Aqaba", PricePerHour = 22, Status = "active", Description = "Beach volleyball and football on the Red Sea shore.", Latitude = 29.5269, Longitude = 35.0082, CreatedAt = new DateTime(2024, 4, 10, 8, 30, 0, DateTimeKind.Utc) },
            new() { Id = "v6", Name = "Petra Squash Center", OwnerId = "u3", City = "Ma'an", Address = "City Center, Ma'an", PricePerHour = 15, Status = "pending", Description = "3 professional squash courts.", Latitude = 30.1983, Longitude = 35.7341, CreatedAt = new DateTime(2024, 4, 20, 9, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v7", Name = "Al-Salt Cricket Ground", OwnerId = "u5", City = "Al-Salt", Address = "Al-Salt Hills", PricePerHour = 35, Status = "active", Description = "Full cricket ground with pavilion.", Latitude = 32.0330, Longitude = 35.7272, CreatedAt = new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc) },
            new() { Id = "v8", Name = "Madaba Aqua Sports", OwnerId = "u2", City = "Madaba", Address = "King's Highway, Madaba", PricePerHour = 40, Status = "active", Description = "Olympic-size indoor swimming pool.", Latitude = 31.7164, Longitude = 35.7934, CreatedAt = new DateTime(2024, 5, 15, 11, 0, 0, DateTimeKind.Utc) },
        };

        // Default operating hours (8am-10pm every day).
        // Keys are full day names to match the dashboard form and the
        // slot-generation endpoint lookup.
        var defaultHours = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "sunday",    new { open = "08:00", close = "22:00" } },
            { "monday",    new { open = "08:00", close = "22:00" } },
            { "tuesday",   new { open = "08:00", close = "22:00" } },
            { "wednesday", new { open = "08:00", close = "22:00" } },
            { "thursday",  new { open = "08:00", close = "22:00" } },
            { "friday",    new { open = "10:00", close = "23:00" } },
            { "saturday",  new { open = "10:00", close = "23:00" } }
        });

        // Set sports, images, operating hours, and cliq aliases
        venues[0].Sports = ["football"];
        venues[0].Images = ["https://picsum.photos/seed/v1a/800/400", "https://picsum.photos/seed/v1b/800/400"];
        venues[0].CliqAlias = "alameen@cliq";
        venues[0].OperatingHoursJson = defaultHours;

        venues[1].Sports = ["basketball", "volleyball"];
        venues[1].Images = ["https://picsum.photos/seed/v2a/800/400", "https://picsum.photos/seed/v2b/800/400"];
        venues[1].CliqAlias = "capitalsports@cliq";
        venues[1].OperatingHoursJson = defaultHours;

        venues[2].Sports = ["tennis", "padel"];
        venues[2].Images = ["https://picsum.photos/seed/v3a/800/400"];
        venues[2].CliqAlias = "zarqatennis@cliq";
        venues[2].OperatingHoursJson = defaultHours;

        venues[3].Sports = ["padel"];
        venues[3].Images = ["https://picsum.photos/seed/v4a/800/400", "https://picsum.photos/seed/v4b/800/400"];
        venues[3].CliqAlias = "northernstar@cliq";
        venues[3].OperatingHoursJson = defaultHours;

        venues[4].Sports = ["volleyball", "football"];
        venues[4].Images = ["https://picsum.photos/seed/v5a/800/400", "https://picsum.photos/seed/v5b/800/400", "https://picsum.photos/seed/v5c/800/400"];
        venues[4].CliqAlias = "aqababeach@cliq";
        venues[4].OperatingHoursJson = defaultHours;

        venues[5].Sports = ["squash"];
        venues[5].Images = ["https://picsum.photos/seed/v6a/800/400"];
        venues[5].CliqAlias = "petrasquash@cliq";
        venues[5].OperatingHoursJson = defaultHours;

        venues[6].Sports = ["cricket"];
        venues[6].Images = ["https://picsum.photos/seed/v7a/800/400", "https://picsum.photos/seed/v7b/800/400"];
        venues[6].CliqAlias = "saltcricket@cliq";
        venues[6].OperatingHoursJson = defaultHours;

        venues[7].Sports = ["swimming"];
        venues[7].Images = ["https://picsum.photos/seed/v8a/800/400", "https://picsum.photos/seed/v8b/800/400"];
        venues[7].CliqAlias = "madabaaqua@cliq";
        venues[7].OperatingHoursJson = defaultHours;

        db.Venues.AddRange(venues);
        await db.SaveChangesAsync();

        var today = DateTime.UtcNow.Date;
        // Helper: calculate 95/5 revenue split
        double fee(double a) => Math.Round(a * 0.05, 2);
        double own(double a) => a - fee(a);

        var bookings = new List<Booking>
        {
            new() { Id = "b1",  VenueId = "v1", PlayerId = "u8",  Sport = "football",   Date = today.AddDays(-29), StartTime = "16:00", Duration = 120, Amount = 50,  TotalAmount = 50,  DepositAmount = 10,  DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(50),  OwnerAmount = own(50),  PaymentProof = "https://picsum.photos/seed/proof1/600/400",  PaymentProofStatus = "approved" },
            new() { Id = "b2",  VenueId = "v2", PlayerId = "u9",  Sport = "basketball",  Date = today.AddDays(-27), StartTime = "10:00", Duration = 60, Amount = 30,  TotalAmount = 30,  DepositAmount = 6,   DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(30),  OwnerAmount = own(30),  PaymentProof = "https://picsum.photos/seed/proof2/600/400",  PaymentProofStatus = "approved" },
            new() { Id = "b3",  VenueId = "v4", PlayerId = "u11", Sport = "padel",       Date = today.AddDays(-25), StartTime = "18:00", Duration = 60, Amount = 18,  TotalAmount = 18,  DepositAmount = 3.6, DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(18),  OwnerAmount = own(18),  PaymentProof = "https://picsum.photos/seed/proof3/600/400",  PaymentProofStatus = "approved" },
            new() { Id = "b4",  VenueId = "v1", PlayerId = "u12", Sport = "football",    Date = today.AddDays(-23), StartTime = "17:00", Duration = 120, Amount = 50,  TotalAmount = 50,  DepositAmount = 10,  DepositPaid = true,  PaymentMethod = "cliq", Status = "cancelled",       SystemFeePercentage = 5, SystemFee = fee(50),  OwnerAmount = own(50),  PaymentProof = "https://picsum.photos/seed/proof4/600/400",  PaymentProofStatus = "approved" },
            new() { Id = "b5",  VenueId = "v5", PlayerId = "u13", Sport = "volleyball",  Date = today.AddDays(-21), StartTime = "09:00", Duration = 120, Amount = 44,  TotalAmount = 44,  DepositAmount = 8.8, DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(44),  OwnerAmount = own(44),  PaymentProof = "https://picsum.photos/seed/proof5/600/400",  PaymentProofStatus = "approved" },
            new() { Id = "b6",  VenueId = "v7", PlayerId = "u14", Sport = "cricket",     Date = today.AddDays(-19), StartTime = "08:00", Duration = 240, Amount = 140, TotalAmount = 140, DepositAmount = 28,  DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(140), OwnerAmount = own(140), PaymentProof = "https://picsum.photos/seed/proof6/600/400",  PaymentProofStatus = "approved" },
            new() { Id = "b7",  VenueId = "v8", PlayerId = "u15", Sport = "swimming",    Date = today.AddDays(-18), StartTime = "08:00", Duration = 60, Amount = 40,  TotalAmount = 40,  DepositAmount = 8,   DepositPaid = true,  PaymentMethod = "cliq", Status = "confirmed",       SystemFeePercentage = 5, SystemFee = fee(40),  OwnerAmount = own(40),  PaymentProof = "https://picsum.photos/seed/proof7/600/400",  PaymentProofStatus = "approved" },
            new() { Id = "b8",  VenueId = "v2", PlayerId = "u8",  Sport = "volleyball",  Date = today.AddDays(-17), StartTime = "15:00", Duration = 120, Amount = 60,  TotalAmount = 60,  DepositAmount = 12,  DepositPaid = true,  PaymentMethod = "cliq", Status = "confirmed",       SystemFeePercentage = 5, SystemFee = fee(60),  OwnerAmount = own(60),  PaymentProof = "https://picsum.photos/seed/proof8/600/400",  PaymentProofStatus = "pending_review" },
            new() { Id = "b9",  VenueId = "v4", PlayerId = "u9",  Sport = "padel",       Date = today.AddDays(-15), StartTime = "19:00", Duration = 60, Amount = 18,  TotalAmount = 18,  DepositAmount = 3.6, DepositPaid = false, PaymentMethod = "cliq", Status = "pending_payment", SystemFeePercentage = 5, SystemFee = fee(18),  OwnerAmount = own(18) },
            new() { Id = "b10", VenueId = "v1", PlayerId = "u11", Sport = "football",    Date = today.AddDays(-14), StartTime = "16:00", Duration = 120, Amount = 50,  TotalAmount = 50,  DepositAmount = 10,  DepositPaid = true,  PaymentMethod = "cliq", Status = "confirmed",       SystemFeePercentage = 5, SystemFee = fee(50),  OwnerAmount = own(50),  PaymentProof = "https://picsum.photos/seed/proof10/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b11", VenueId = "v5", PlayerId = "u12", Sport = "football",    Date = today.AddDays(-13), StartTime = "10:00", Duration = 60, Amount = 22,  TotalAmount = 22,  DepositAmount = 4.4, DepositPaid = false, PaymentMethod = "cliq", Status = "pending_payment", SystemFeePercentage = 5, SystemFee = fee(22),  OwnerAmount = own(22),  PaymentProof = "https://picsum.photos/seed/proof11/600/400", PaymentProofStatus = "pending_review" },
            new() { Id = "b12", VenueId = "v7", PlayerId = "u13", Sport = "cricket",     Date = today.AddDays(-11), StartTime = "08:00", Duration = 180, Amount = 105, TotalAmount = 105, DepositAmount = 21,  DepositPaid = true,  PaymentMethod = "cliq", Status = "confirmed",       SystemFeePercentage = 5, SystemFee = fee(105), OwnerAmount = own(105), PaymentProof = "https://picsum.photos/seed/proof12/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b13", VenueId = "v8", PlayerId = "u14", Sport = "swimming",    Date = today.AddDays(-10), StartTime = "08:00", Duration = 120, Amount = 80,  TotalAmount = 80,  DepositAmount = 16,  DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(80),  OwnerAmount = own(80),  PaymentProof = "https://picsum.photos/seed/proof13/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b14", VenueId = "v2", PlayerId = "u15", Sport = "basketball",  Date = today.AddDays(-9),  StartTime = "17:00", Duration = 60, Amount = 30,  TotalAmount = 30,  DepositAmount = 6,   DepositPaid = true,  PaymentMethod = "cliq", Status = "cancelled",       SystemFeePercentage = 5, SystemFee = fee(30),  OwnerAmount = own(30),  PaymentProof = "https://picsum.photos/seed/proof14/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b15", VenueId = "v1", PlayerId = "u8",  Sport = "football",    Date = today.AddDays(-8),  StartTime = "18:00", Duration = 120, Amount = 50,  TotalAmount = 50,  DepositAmount = 10,  DepositPaid = false, PaymentMethod = "cliq", Status = "pending_payment", SystemFeePercentage = 5, SystemFee = fee(50),  OwnerAmount = own(50),  PaymentProof = "https://picsum.photos/seed/proof15/600/400", PaymentProofStatus = "pending_review" },
            new() { Id = "b16", VenueId = "v4", PlayerId = "u9",  Sport = "padel",       Date = today.AddDays(-7),  StartTime = "20:00", Duration = 60, Amount = 18,  TotalAmount = 18,  DepositAmount = 3.6, DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(18),  OwnerAmount = own(18),  PaymentProof = "https://picsum.photos/seed/proof16/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b17", VenueId = "v5", PlayerId = "u11", Sport = "volleyball",  Date = today.AddDays(-6),  StartTime = "09:00", Duration = 120, Amount = 44,  TotalAmount = 44,  DepositAmount = 8.8, DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(44),  OwnerAmount = own(44),  PaymentProof = "https://picsum.photos/seed/proof17/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b18", VenueId = "v7", PlayerId = "u12", Sport = "cricket",     Date = today.AddDays(-5),  StartTime = "08:00", Duration = 240, Amount = 140, TotalAmount = 140, DepositAmount = 28,  DepositPaid = true,  PaymentMethod = "cliq", Status = "confirmed",       SystemFeePercentage = 5, SystemFee = fee(140), OwnerAmount = own(140), PaymentProof = "https://picsum.photos/seed/proof18/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b19", VenueId = "v8", PlayerId = "u13", Sport = "swimming",    Date = today.AddDays(-4),  StartTime = "08:00", Duration = 60, Amount = 40,  TotalAmount = 40,  DepositAmount = 8,   DepositPaid = false, PaymentMethod = "cliq", Status = "pending_payment", SystemFeePercentage = 5, SystemFee = fee(40),  OwnerAmount = own(40) },
            new() { Id = "b20", VenueId = "v2", PlayerId = "u14", Sport = "basketball",  Date = today.AddDays(-3),  StartTime = "14:00", Duration = 120, Amount = 60,  TotalAmount = 60,  DepositAmount = 12,  DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(60),  OwnerAmount = own(60),  PaymentProof = "https://picsum.photos/seed/proof20/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b21", VenueId = "v1", PlayerId = "u15", Sport = "football",    Date = today.AddDays(-2),  StartTime = "16:00", Duration = 60, Amount = 25,  TotalAmount = 25,  DepositAmount = 5,   DepositPaid = true,  PaymentMethod = "cliq", Status = "confirmed",       SystemFeePercentage = 5, SystemFee = fee(25),  OwnerAmount = own(25),  PaymentProof = "https://picsum.photos/seed/proof21/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b22", VenueId = "v4", PlayerId = "u8",  Sport = "padel",       Date = today.AddDays(-2),  StartTime = "19:00", Duration = 120, Amount = 36,  TotalAmount = 36,  DepositAmount = 7.2, DepositPaid = true,  PaymentMethod = "cliq", Status = "completed",       SystemFeePercentage = 5, SystemFee = fee(36),  OwnerAmount = own(36),  PaymentProof = "https://picsum.photos/seed/proof22/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b23", VenueId = "v5", PlayerId = "u9",  Sport = "football",    Date = today.AddDays(-1),  StartTime = "10:00", Duration = 60, Amount = 22,  TotalAmount = 22,  DepositAmount = 4.4, DepositPaid = false, PaymentMethod = "cliq", Status = "pending_payment", SystemFeePercentage = 5, SystemFee = fee(22),  OwnerAmount = own(22),  PaymentProof = "https://picsum.photos/seed/proof23/600/400", PaymentProofStatus = "pending_review" },
            new() { Id = "b24", VenueId = "v7", PlayerId = "u11", Sport = "cricket",     Date = today.AddDays(-1),  StartTime = "08:00", Duration = 180, Amount = 105, TotalAmount = 105, DepositAmount = 21,  DepositPaid = true,  PaymentMethod = "cliq", Status = "confirmed",       SystemFeePercentage = 5, SystemFee = fee(105), OwnerAmount = own(105), PaymentProof = "https://picsum.photos/seed/proof24/600/400", PaymentProofStatus = "approved" },
            new() { Id = "b25", VenueId = "v8", PlayerId = "u12", Sport = "swimming",    Date = today,              StartTime = "08:00", Duration = 60, Amount = 40,  TotalAmount = 40,  DepositAmount = 8,   DepositPaid = false, PaymentMethod = "cliq", Status = "pending_payment", SystemFeePercentage = 5, SystemFee = fee(40),  OwnerAmount = own(40) },
        };

        db.Bookings.AddRange(bookings);
        await db.SaveChangesAsync();

        var payments = new List<Payment>
        {
            new() { Id = "p1",  BookingId = "b1",  PlayerId = "u8",  Amount = 50,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-29).AddHours(16) },
            new() { Id = "p2",  BookingId = "b2",  PlayerId = "u9",  Amount = 30,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-27).AddHours(10) },
            new() { Id = "p3",  BookingId = "b3",  PlayerId = "u11", Amount = 18,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-25).AddHours(18) },
            new() { Id = "p4",  BookingId = "b4",  PlayerId = "u12", Amount = 50,  Method = "Cliq", Status = "refunded", Date = today.AddDays(-23).AddHours(17) },
            new() { Id = "p5",  BookingId = "b5",  PlayerId = "u13", Amount = 44,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-21).AddHours(9) },
            new() { Id = "p6",  BookingId = "b6",  PlayerId = "u14", Amount = 140, Method = "Cliq", Status = "paid",     Date = today.AddDays(-19).AddHours(8) },
            new() { Id = "p7",  BookingId = "b7",  PlayerId = "u15", Amount = 40,  Method = "Cliq", Status = "pending",  Date = today.AddDays(-18).AddHours(7) },
            new() { Id = "p8",  BookingId = "b8",  PlayerId = "u8",  Amount = 60,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-17).AddHours(15) },
            new() { Id = "p9",  BookingId = "b9",  PlayerId = "u9",  Amount = 18,  Method = "Cliq", Status = "pending",  Date = today.AddDays(-15).AddHours(19) },
            new() { Id = "p10", BookingId = "b10", PlayerId = "u11", Amount = 50,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-14).AddHours(16) },
            new() { Id = "p11", BookingId = "b11", PlayerId = "u12", Amount = 22,  Method = "Cliq", Status = "pending",  Date = today.AddDays(-13).AddHours(10) },
            new() { Id = "p12", BookingId = "b12", PlayerId = "u13", Amount = 105, Method = "Cliq", Status = "paid",     Date = today.AddDays(-11).AddHours(8) },
            new() { Id = "p13", BookingId = "b13", PlayerId = "u14", Amount = 80,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-10).AddHours(6) },
            new() { Id = "p14", BookingId = "b14", PlayerId = "u15", Amount = 30,  Method = "Cliq", Status = "refunded", Date = today.AddDays(-9).AddHours(17) },
            new() { Id = "p15", BookingId = "b15", PlayerId = "u8",  Amount = 50,  Method = "Cliq", Status = "pending",  Date = today.AddDays(-8).AddHours(18) },
            new() { Id = "p16", BookingId = "b16", PlayerId = "u9",  Amount = 18,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-7).AddHours(20) },
            new() { Id = "p17", BookingId = "b17", PlayerId = "u11", Amount = 44,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-6).AddHours(9) },
            new() { Id = "p18", BookingId = "b18", PlayerId = "u12", Amount = 140, Method = "Cliq", Status = "paid",     Date = today.AddDays(-5).AddHours(8) },
            new() { Id = "p19", BookingId = "b19", PlayerId = "u13", Amount = 40,  Method = "Cliq", Status = "pending",  Date = today.AddDays(-4).AddHours(7) },
            new() { Id = "p20", BookingId = "b20", PlayerId = "u14", Amount = 60,  Method = "Cliq", Status = "paid",     Date = today.AddDays(-3).AddHours(14) },
        };

        db.Payments.AddRange(payments);
        await db.SaveChangesAsync();

        // Reviews — only for (player, venue) pairs that have at least one completed booking.
        // Unique on (PlayerId, VenueId).
        var reviews = new List<Review>
        {
            new() { Id = "r1",  PlayerId = "u8",  VenueId = "v1", Rating = 5, Comment = "Excellent pitch, lights were perfect for evening matches.",     CreatedAt = today.AddDays(-28), UpdatedAt = today.AddDays(-28) },
            new() { Id = "r2",  PlayerId = "u9",  VenueId = "v2", Rating = 4, Comment = "Clean indoor court and friendly staff. Would book again.",      CreatedAt = today.AddDays(-26), UpdatedAt = today.AddDays(-26) },
            new() { Id = "r3",  PlayerId = "u11", VenueId = "v4", Rating = 5, Comment = "AC made a huge difference. Best padel courts in Irbid.",        CreatedAt = today.AddDays(-24), UpdatedAt = today.AddDays(-24) },
            new() { Id = "r4",  PlayerId = "u13", VenueId = "v5", Rating = 4, Comment = "جو رائع على شاطئ البحر الأحمر، تنظيم ممتاز.",                    CreatedAt = today.AddDays(-20), UpdatedAt = today.AddDays(-20) },
            new() { Id = "r5",  PlayerId = "u14", VenueId = "v7", Rating = 5, Comment = "Full size ground with a proper pavilion. Highly recommended.",  CreatedAt = today.AddDays(-18), UpdatedAt = today.AddDays(-18) },
            new() { Id = "r6",  PlayerId = "u14", VenueId = "v8", Rating = 5, Comment = "ملعب رائع ومياه نظيفة جداً.",                                    CreatedAt = today.AddDays(-9),  UpdatedAt = today.AddDays(-9) },
            new() { Id = "r7",  PlayerId = "u9",  VenueId = "v4", Rating = 4, Comment = "Good value. Court surface could use a touch-up but overall fun.", CreatedAt = today.AddDays(-6),  UpdatedAt = today.AddDays(-6) },
            new() { Id = "r8",  PlayerId = "u11", VenueId = "v5", Rating = 3, Comment = "Nice location, but parking is tight on weekends.",              CreatedAt = today.AddDays(-5),  UpdatedAt = today.AddDays(-5) },
            new() { Id = "r9",  PlayerId = "u14", VenueId = "v2", Rating = 4, Comment = "Solid basketball setup. Will return with friends.",             CreatedAt = today.AddDays(-2),  UpdatedAt = today.AddDays(-2) },
            new() { Id = "r10", PlayerId = "u8",  VenueId = "v4", Rating = 5, Comment = "تجربة ممتازة والخدمة سريعة.",                                    CreatedAt = today.AddDays(-1),  UpdatedAt = today.AddDays(-1) },
        };

        db.Reviews.AddRange(reviews);
        await db.SaveChangesAsync();

        Console.WriteLine($"Seed complete: {users.Count} users, {venues.Count} venues, {bookings.Count} bookings, {payments.Count} payments, {reviews.Count} reviews");
    }
}
