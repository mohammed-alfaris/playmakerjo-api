using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Models;

namespace SportsVenueApi.Data;

/// <summary>
/// Two demo companies for walking through billing on a live database:
/// <list type="bullet">
/// <item>"DEMO Overdue Arena" — trial long over, one paid invoice and two issued ones past their
/// due date, so the overdue badge, the owner's banner and suspension can be tried.</item>
/// <item>"DEMO Active Club" — trial over, two venues and months of app and counter bookings, and
/// no invoices yet, so invoices can be generated month by month.</item>
/// </list>
/// Additive and idempotent like <see cref="DemoOwnerSeed"/>: every row it writes has an id
/// starting <see cref="Prefix"/>, a second run does nothing, and <see cref="RemoveAsync"/>
/// deletes all of it (and whatever was done to it since) without touching anything else.
/// Demo invoices are numbered DEMO-…, never PMJ-…, so the real invoice sequence is untouched.
///
/// Run on the server: `dotnet SportsVenueApi.dll --seed-demo-billing`, and
/// `--remove-demo-billing` to take it all away.
/// </summary>
public static class DemoBillingSeed
{
    public const string Prefix = "demo_bill_";

    private const string OverdueOwner = Prefix + "own1";
    private const string ActiveOwner = Prefix + "own2";
    private const string Player = Prefix + "player";
    private static readonly string[] Owners = [OverdueOwner, ActiveOwner];

    public static async Task<string> RunAsync(AppDbContext db, double feePercent)
    {
        if (await db.Users.AnyAsync(u => u.Id == OverdueOwner))
            return "[demo-billing] already present — nothing to do.";

        var today = PlatformConstants.JordanToday();
        var thisMonth = new DateTime(today.Year, today.Month, 1);

        // People. Passwords are random and unknown: sign in as them by resetting the password
        // from Users, or look through their eyes with "View as" on Companies.
        db.Users.AddRange(
            DemoUser(OverdueOwner, "DEMO Overdue Owner", "demo.overdue@playmakerjo.invalid", "venue_owner"),
            DemoUser(ActiveOwner, "DEMO Active Owner", "demo.active@playmakerjo.invalid", "venue_owner"),
            DemoUser(Player, "DEMO Player", "demo.player@playmakerjo.invalid", "player"));

        db.Companies.AddRange(
            new Company
            {
                OwnerId = OverdueOwner, Name = "DEMO Overdue Arena", BillingCycle = "monthly",
                TrialEndsOn = thisMonth.AddMonths(-4).AddDays(14), CreatedAt = DateTime.UtcNow.AddMonths(-5),
            },
            new Company
            {
                OwnerId = ActiveOwner, Name = "DEMO Active Club", BillingCycle = "monthly",
                TrialEndsOn = thisMonth.AddMonths(-4).AddDays(-1), CreatedAt = DateTime.UtcNow.AddMonths(-5),
            });

        var venues = new[]
        {
            DemoVenue(Prefix + "v1", OverdueOwner, "DEMO Overdue Arena", 20, 31.99, 35.86),
            DemoVenue(Prefix + "v2", ActiveOwner, "DEMO Active Club — North", 25, 32.02, 35.87),
            DemoVenue(Prefix + "v3", ActiveOwner, "DEMO Active Club — South", 30, 31.93, 35.93, pitches: 3),
        };
        db.Venues.AddRange(venues);
        await db.SaveChangesAsync();

        var customers = new List<Customer>();
        foreach (var owner in Owners)
            for (var i = 1; i <= 4; i++)
                customers.Add(new Customer
                {
                    Id = $"{Prefix}cus_{owner[^1]}{i}", OwnerId = owner, Name = $"DEMO Customer {i}",
                    Phone = $"+96279900{owner[^1]}{i:00}0", Status = "active", CreatedByUserId = owner,
                    CreatedAt = DateTime.UtcNow.AddMonths(-5),
                });
        db.Customers.AddRange(customers);
        await db.SaveChangesAsync();

        // Bookings: from five months back to a few days ahead. Every week each venue gets two
        // app bookings (these carry the commission) and three counter bookings.
        var bookings = new List<Booking>();
        var payments = new List<Payment>();
        var seq = 0;
        var start = thisMonth.AddMonths(-5);
        var rnd = new Random(7);
        foreach (var venue in venues)
        {
            var venueCustomers = customers.Where(c => c.OwnerId == venue.OwnerId).ToList();
            for (var day = start; day <= today.AddDays(5); day = day.AddDays(1))
            {
                var dow = (int)day.DayOfWeek;
                var app = dow is 1 or 4;                 // Monday and Thursday from the app
                var counter = dow is 0 or 2 or 5;        // Sunday, Tuesday, Friday at the counter
                if (!app && !counter) continue;

                var past = day < today;
                var hours = rnd.Next(1, 3);
                var total = venue.PricePerHour * hours;
                var fee = app ? Math.Round(total * feePercent / 100, 3) : 0;
                var status = !past ? "confirmed" : rnd.Next(12) == 0 ? "no_show" : "completed";
                var customer = counter ? venueCustomers[rnd.Next(venueCustomers.Count)] : null;
                var paid = past ? total : Math.Round(total * venue.DepositPercentage / 100, 3);

                var id = $"{Prefix}bk{++seq:0000}";
                bookings.Add(new Booking
                {
                    Id = id, VenueId = venue.Id, PlayerId = app ? Player : venue.OwnerId, CustomerId = customer?.Id,
                    Sport = "football", Date = day, StartTime = $"{17 + rnd.Next(0, 4)}:00", Duration = hours * 60,
                    Amount = total, TotalAmount = total, DepositAmount = Math.Round(total * venue.DepositPercentage / 100, 3),
                    DepositPaid = true, AmountPaid = paid, SystemFeePercentage = app ? feePercent : 0, SystemFee = fee,
                    OwnerAmount = total - fee, PaymentMethod = "cliq", IsManual = !app, Status = status,
                    Notes = app ? null : $"[MANUAL] [football]", CreatedAt = day.AddDays(-2),
                });
                payments.Add(new Payment
                {
                    Id = $"{Prefix}pay{seq:0000}", BookingId = id, PlayerId = app ? Player : venue.OwnerId,
                    CustomerId = customer?.Id, VenueId = venue.Id, RecordedByUserId = venue.OwnerId,
                    Amount = paid, Method = app ? "cliq" : "cash", Kind = past ? "full" : "deposit", Status = "paid",
                    Date = day.AddDays(-1),
                });
            }
        }
        db.Bookings.AddRange(bookings);
        await db.SaveChangesAsync();
        db.Payments.AddRange(payments);
        await db.SaveChangesAsync();

        // The overdue company's invoices: three months ago paid, the two since issued and unpaid.
        var invoices = new List<Invoice>();
        for (var back = 3; back >= 1; back--)
        {
            var month = thisMonth.AddMonths(-back);
            var commissionBookings = bookings.Where(b => b.VenueId == Prefix + "v1" && !b.IsManual
                && b.Date >= month.AddMonths(-1) && b.Date < month).ToList();
            var commission = Math.Round(commissionBookings.Sum(b => b.SystemFee), 3);
            var paid = back == 3;
            var invoice = new Invoice
            {
                Id = $"{Prefix}inv{back}", Number = $"DEMO-{month:yyyy}-{4 - back:0000}", OwnerId = OverdueOwner,
                Period = month.ToString("yyyy-MM"), Status = paid ? "paid" : "issued",
                IssuedAt = month.AddDays(1), DueOn = month.AddDays(14),
                PaidAt = paid ? month.AddDays(10) : null, PaidMethod = paid ? "cliq" : null,
                PaidReference = paid ? "DEMO-REF" : null, CreatedAt = month.AddDays(1),
                Lines =
                [
                    new InvoiceLine
                    {
                        Id = $"{Prefix}line{back}a", Kind = "subscription",
                        Description = $"Monthly subscription ({month:yyyy-MM}): 1 venue(s) with up to 2 pitch(es)",
                        DescriptionAr = $"الاشتراك الشهري ({month:yyyy-MM}): 1 منشأة حتى 2 ملعب",
                        Quantity = 1, UnitPrice = 50, Amount = 50, CoversFrom = month, CoversTo = month.AddMonths(1).AddDays(-1), Sort = 0,
                    },
                ],
            };
            if (commission > 0)
                invoice.Lines.Add(new InvoiceLine
                {
                    Id = $"{Prefix}line{back}b", Kind = "commission",
                    Description = $"Commission on {commissionBookings.Count} app booking(s) in {month.AddMonths(-1):yyyy-MM}",
                    DescriptionAr = $"عمولة {commissionBookings.Count} حجز من التطبيق في {month.AddMonths(-1):yyyy-MM}",
                    Quantity = commissionBookings.Count, UnitPrice = Math.Round(commission / commissionBookings.Count, 3),
                    Amount = commission, Sort = 1,
                });
            invoice.Total = Math.Round(invoice.Lines.Sum(l => l.Amount), 3);
            invoices.Add(invoice);
        }
        db.Invoices.AddRange(invoices);
        await db.SaveChangesAsync();

        return "[demo-billing] created:\n"
            + "  DEMO Overdue Arena — 1 venue, 1 paid and 2 overdue invoices\n"
            + "  DEMO Active Club   — 2 venues, no invoices yet\n"
            + $"  {bookings.Count} bookings and {payments.Count} payments from {start:yyyy-MM-dd} to {today.AddDays(5):yyyy-MM-dd}";
    }

    /// <summary>
    /// Deletes every demo row and everything hanging off the demo owners, venues and player —
    /// including invoices, staff, roles and notifications created by trying things out. The
    /// activity log is append-only by design, so entries about the demo companies stay.
    /// </summary>
    public static async Task<string> RemoveAsync(AppDbContext db)
    {
        var people = await db.Users
            .Where(u => u.Id.StartsWith(Prefix) || (u.ManagedByOwnerId != null && Owners.Contains(u.ManagedByOwnerId)))
            .Select(u => u.Id).ToListAsync();
        var venueIds = await db.Venues.Where(v => Owners.Contains(v.OwnerId)).Select(v => v.Id).ToListAsync();
        var bookingIds = await db.Bookings.Where(b => venueIds.Contains(b.VenueId) || b.PlayerId == Player).Select(b => b.Id).ToListAsync();

        var n = 0;
        n += await db.Notifications.Where(x => people.Contains(x.UserId) || (x.ReferenceId != null && bookingIds.Contains(x.ReferenceId))).ExecuteDeleteAsync();
        n += await db.DeviceTokens.Where(x => people.Contains(x.UserId)).ExecuteDeleteAsync();
        n += await db.Favorites.Where(x => people.Contains(x.UserId) || venueIds.Contains(x.VenueId)).ExecuteDeleteAsync();
        n += await db.Reviews.Where(x => venueIds.Contains(x.VenueId) || people.Contains(x.PlayerId)).ExecuteDeleteAsync();
        n += await db.Payments.Where(x => bookingIds.Contains(x.BookingId)).ExecuteDeleteAsync();
        n += await db.Bookings.Where(x => bookingIds.Contains(x.Id)).ExecuteDeleteAsync();
        n += await db.RecurringBookingGroups.Where(x => venueIds.Contains(x.VenueId)).ExecuteDeleteAsync();
        n += await db.PermanentBookings.Where(x => venueIds.Contains(x.VenueId)).ExecuteDeleteAsync();
        n += await db.VenueBlocks.Where(x => venueIds.Contains(x.VenueId)).ExecuteDeleteAsync();
        n += await db.InvoiceLines.Where(x => Owners.Contains(x.Invoice.OwnerId)).ExecuteDeleteAsync();
        n += await db.Invoices.Where(x => Owners.Contains(x.OwnerId)).ExecuteDeleteAsync();
        n += await db.Customers.Where(x => Owners.Contains(x.OwnerId)).ExecuteDeleteAsync();
        n += await db.Venues.Where(x => venueIds.Contains(x.Id)).ExecuteDeleteAsync();
        n += await db.Companies.Where(x => Owners.Contains(x.OwnerId)).ExecuteDeleteAsync();
        await db.Users.Where(x => people.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.StaffRoleId, (string?)null));
        n += await db.StaffRoles.Where(x => Owners.Contains(x.OwnerId)).ExecuteDeleteAsync();
        n += await db.Users.Where(x => people.Contains(x.Id)).ExecuteDeleteAsync();

        return $"[demo-billing] removed {n} rows.";
    }

    private static User DemoUser(string id, string name, string email, string role) => new()
    {
        Id = id, Name = name, Email = email, Phone = null, Role = role, Status = "active",
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N")),
        CreatedAt = DateTime.UtcNow.AddMonths(-5),
    };

    private static Venue DemoVenue(string id, string ownerId, string name, double price, double lat, double lng, int pitches = 1)
    {
        var hours = System.Text.Json.JsonSerializer.Serialize(
            new[] { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" }
                .ToDictionary(d => d, _ => new { open = "08:00", close = "23:00" }));
        var venue = new Venue
        {
            Id = id, OwnerId = ownerId, Name = name, City = "Amman", Address = "Demo data — not a real venue",
            PricePerHour = price, DepositPercentage = 20, Status = "active", Latitude = lat, Longitude = lng,
            CliqAlias = "demo@cliq", OperatingHoursJson = hours, CreatedAt = DateTime.UtcNow.AddMonths(-5),
        };
        venue.Sports = ["football"];
        // Three pitches or more makes a "large" venue, billed at the higher price.
        if (pitches > 1)
            venue.Pitches = Enumerable.Range(1, pitches).Select(i => new DTOs.Venues.PitchDto
            {
                Id = $"{id}_p{i}", Name = $"Pitch {i}", Sport = "football", PricePerHour = price,
            }).ToList();
        return venue;
    }
}
