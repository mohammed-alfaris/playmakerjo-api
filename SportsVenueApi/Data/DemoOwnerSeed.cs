using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Models;

namespace SportsVenueApi.Data;

/// <summary>
/// Seeds ONE demo venue_owner with a venue, a customer book, and a week of bookings, so the
/// new CRM feature (Customer, PaymentLedger, IsManual) can be clicked through on a database
/// that otherwise holds nothing but the platform admin.
///
/// Deliberately NOT <see cref="SeedData"/>: that path drops the entire database and refuses
/// to run in Production at all. This is the opposite shape — additive only, safe to run
/// against a live database, and a no-op on a second run. It never touches an existing row;
/// it only checks whether its own owner email is already present and, if so, does nothing.
///
/// Invoked via `dotnet SportsVenueApi.dll --seed-demo-owner` (see Program.cs). Not wired to
/// any HTTP route — an admin runs it once from the deploy box, the same way `--seed` is run.
/// </summary>
public static class DemoOwnerSeed
{
    private readonly record struct Cust(string Id, string Name, string Phone, string? Note);

    private readonly record struct Bk(
        string CustomerId, int DayOffset, string StartTime, int Duration,
        double TotalAmount, string Status, PayState Pay);

    /// <summary>How much of the total has actually been received, at seed time.</summary>
    private enum PayState { None, DepositOnly, Full }

    public static async Task<string> Run(AppDbContext db, string ownerEmail, string ownerPasswordPlain)
    {
        if (await db.Users.AnyAsync(u => u.Email == ownerEmail))
            return $"[demo-seed] {ownerEmail} already exists — nothing to do (idempotent no-op).";

        var today = PlatformConstants.JordanToday();
        const string ownerId = "demo_owner";
        const string venueId = "demo_v1";

        var owner = new User
        {
            Id = ownerId,
            Name = "Yousef Trad",
            Email = ownerEmail,
            Phone = "+962791234567",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(ownerPasswordPlain),
            Role = "venue_owner",
            Status = "active",
            Avatar = "https://api.dicebear.com/7.x/avataaars/svg?seed=Yousef",
            CreatedAt = DateTime.UtcNow.AddDays(-90),
        };
        db.Users.Add(owner);
        await db.SaveChangesAsync();

        var operatingHours = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            { "sunday",    new { open = "08:00", close = "22:00" } },
            { "monday",    new { open = "08:00", close = "22:00" } },
            { "tuesday",   new { open = "08:00", close = "22:00" } },
            { "wednesday", new { open = "08:00", close = "22:00" } },
            { "thursday",  new { open = "08:00", close = "22:00" } },
            { "friday",    new { open = "10:00", close = "23:00" } },
            { "saturday",  new { open = "10:00", close = "23:00" } },
        });

        var venue = new Venue
        {
            Id = venueId,
            OwnerId = ownerId,
            Name = "Downtown Kickoff Arena",
            City = "Amman",
            Address = "Jabal Amman, 3rd Circle",
            PricePerHour = 25,
            Status = "active",
            Description = "5-a-side and full-size football pitch in the heart of Amman.",
            Latitude = 31.9539,
            Longitude = 35.9106,
            CliqAlias = "downtownkickoff@cliq",
            OperatingHoursJson = operatingHours,
            CreatedAt = DateTime.UtcNow.AddDays(-80),
        };
        venue.Sports = ["football"];
        venue.Images = [
            "https://picsum.photos/seed/demo-v1a/800/400",
            "https://picsum.photos/seed/demo-v1b/800/400",
        ];
        db.Venues.Add(venue);
        await db.SaveChangesAsync();

        // 10 customers, phone-keyed, covering the CRM segments the dashboard's Customers
        // tab actually filters on: regulars (>=4 visits/90d), unreliable (>=2 no-shows),
        // owing (unpaid balance), new (<=1 visit).
        var customers = new List<Cust>
        {
            new("cus_demo01", "Ahmad Zubi",      "+962791111111", "Books the 8pm slot most weeks"),
            new("cus_demo02", "Sara Odeh",        "+962791111112", null),
            new("cus_demo03", "Omar Btoush",      "+962791111113", "Pays balance in cash at the gate"),
            new("cus_demo04", "Lina Freij",       "+962791111114", null),
            new("cus_demo05", "Mazen Halasa",     "+962791111115", "Prefers the far pitch"),
            new("cus_demo06", "Rana Qasem",       "+962791111116", null),
            new("cus_demo07", "Bassel Nabulsi",   "+962791111117", null),
            new("cus_demo08", "Dana Kayed",       "+962791111118", "First time — walked in off the street"),
            new("cus_demo09", "Huda Sarayrah",    "+962791111119", "Runs a weekly five-a-side group"),
            new("cus_demo10", "Firas Qutub",      "+962791111120", "Flaky — confirm by phone before the slot"),
        };

        // 21 bookings, all within the last week plus a few days out (upcoming). All are
        // counter bookings (IsManual=true) — that is the only path that populates a
        // Customer, so a demo book with an "app" channel would be fiction. Fee is 0% on
        // every row for the same reason (manual bookings never carry the platform fee).
        var plan = new List<Bk>
        {
            new("cus_demo01", -6, "18:00", 60,  25,   "completed",       PayState.Full),
            new("cus_demo01", -4, "18:00", 60,  25,   "completed",       PayState.Full),
            new("cus_demo01", -2, "18:00", 60,  25,   "completed",       PayState.Full),
            new("cus_demo01",  1, "19:00", 60,  25,   "confirmed",       PayState.DepositOnly),

            new("cus_demo02", -5, "17:00", 90,  37.5, "completed",       PayState.Full),
            new("cus_demo02", -1, "17:00", 90,  37.5, "no_show",         PayState.DepositOnly),

            new("cus_demo03", -3, "20:00", 120, 50,   "completed",       PayState.DepositOnly),

            new("cus_demo04", -6, "16:00", 60,  25,   "cancelled",       PayState.None),

            new("cus_demo05", -7, "09:00", 60,  25,   "completed",       PayState.Full),
            new("cus_demo05", -4, "09:00", 60,  25,   "completed",       PayState.Full),
            new("cus_demo05",  2, "09:00", 60,  25,   "confirmed",       PayState.DepositOnly),

            new("cus_demo06", -2, "21:00", 90,  37.5, "pending_payment", PayState.None),

            new("cus_demo07", -2, "15:00", 60,  25,   "completed",       PayState.Full),
            new("cus_demo07",  1, "15:00", 60,  25,   "confirmed",       PayState.DepositOnly),

            new("cus_demo08", -1, "19:00", 60,  25,   "completed",       PayState.Full),

            new("cus_demo09", -6, "10:00", 120, 50,   "completed",       PayState.Full),
            new("cus_demo09", -3, "10:00", 120, 50,   "completed",       PayState.Full),
            new("cus_demo09", -1, "10:00", 120, 50,   "completed",       PayState.Full),
            new("cus_demo09",  0, "10:00", 120, 50,   "confirmed",       PayState.DepositOnly),

            new("cus_demo10", -4, "14:00", 60,  25,   "no_show",         PayState.DepositOnly),
            new("cus_demo10", -2, "14:00", 60,  25,   "no_show",         PayState.DepositOnly),
        };

        var customerRows = customers.Select(c => new Customer
        {
            Id = c.Id,
            OwnerId = ownerId,
            Phone = c.Phone,
            Name = c.Name,
            Note = c.Note,
            Status = "active",
            CreatedByUserId = ownerId,
            CreatedAt = DateTime.UtcNow.AddDays(plan.Where(b => b.CustomerId == c.Id).Min(b => b.DayOffset) - 1),
        }).ToList();
        db.Customers.AddRange(customerRows);
        await db.SaveChangesAsync();

        var bookingRows = new List<Booking>();
        var paymentRows = new List<Payment>();
        var seq = 0;

        foreach (var b in plan)
        {
            var deposit = Math.Round(b.TotalAmount * (venue.DepositPercentage / 100.0), 3);
            var paid = b.Pay switch
            {
                PayState.Full => b.TotalAmount,
                PayState.DepositOnly => deposit,
                _ => 0,
            };

            var bookingId = $"demo_bk{++seq:D2}";
            var date = today.AddDays(b.DayOffset);

            var booking = new Booking
            {
                Id = bookingId,
                VenueId = venueId,
                PlayerId = ownerId,       // counter booking — see Payment.PlayerId doc on the model
                CustomerId = b.CustomerId,
                Sport = "football",
                Date = date,
                StartTime = b.StartTime,
                Duration = b.Duration,
                Amount = b.TotalAmount,
                TotalAmount = b.TotalAmount,
                DepositAmount = deposit,
                DepositPaid = paid > 0,
                AmountPaid = paid,
                SystemFeePercentage = 0,
                SystemFee = 0,
                OwnerAmount = b.TotalAmount,
                PaymentMethod = "cliq",
                IsManual = true,
                Status = b.Status,
                Notes = $"[MANUAL] [football] Walk-in: {customers.First(c => c.Id == b.CustomerId).Name}.",
                CreatedAt = date.AddHours(-2),
            };
            bookingRows.Add(booking);

            if (paid > 0)
            {
                paymentRows.Add(new Payment
                {
                    Id = $"demo_pay{seq:D2}",
                    BookingId = bookingId,
                    PlayerId = ownerId,
                    CustomerId = b.CustomerId,
                    VenueId = venueId,
                    RecordedByUserId = ownerId,
                    Amount = paid,
                    Method = "cash",
                    Kind = b.Pay == PayState.Full ? "full" : "deposit",
                    Status = "paid",
                    Note = b.Pay == PayState.Full ? "Paid in full at the counter" : "Deposit taken at booking",
                    Date = date.AddHours(-2),
                });
            }
        }

        db.Bookings.AddRange(bookingRows);
        await db.SaveChangesAsync();
        db.Payments.AddRange(paymentRows);
        await db.SaveChangesAsync();

        return "[demo-seed] created:\n"
            + $"  owner   {owner.Email} / (password as provided)\n"
            + $"  venue   {venue.Name} ({venue.Id})\n"
            + $"  customers  {customerRows.Count}\n"
            + $"  bookings   {bookingRows.Count} (window: {today.AddDays(-7):yyyy-MM-dd} .. {today.AddDays(2):yyyy-MM-dd})\n"
            + $"  payments   {paymentRows.Count}";
    }
}
