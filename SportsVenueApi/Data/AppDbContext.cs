using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Models;

namespace SportsVenueApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<RecurringBookingGroup> RecurringBookingGroups => Set<RecurringBookingGroup>();
    public DbSet<PermanentBooking> PermanentBookings => Set<PermanentBooking>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<NotificationTemplate> NotificationTemplates => Set<NotificationTemplate>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();
    public DbSet<PlayerWaitlist> PlayerWaitlist => Set<PlayerWaitlist>();
    public DbSet<VenueWaitlist> VenueWaitlist => Set<VenueWaitlist>();
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Venue>(e =>
        {
            e.HasOne(v => v.Owner)
             .WithMany()
             .HasForeignKey(v => v.OwnerId);
        });

        modelBuilder.Entity<Booking>(e =>
        {
            e.HasOne(b => b.Venue)
             .WithMany()
             .HasForeignKey(b => b.VenueId);

            e.HasOne(b => b.Player)
             .WithMany()
             .HasForeignKey(b => b.PlayerId);

            // Optional, and SetNull on delete: archiving is the normal path, but if a
            // customer row ever is removed the booking must survive — it is a financial
            // record. Explicit because EF's default for an optional FK is ClientSetNull,
            // which only works for entities already tracked in memory.
            e.HasOne(b => b.Customer)
             .WithMany()
             .HasForeignKey(b => b.CustomerId)
             .OnDelete(DeleteBehavior.SetNull);

            // Customer history is read per customer, newest first, on every detail view.
            // bookings had no index covering this at all.
            e.HasIndex(b => new { b.CustomerId, b.Date });

            // The expiry sweep's only query. Leading on the nullable deadline is deliberate:
            // MySQL does not index NULLs for range scans, and NULL is the overwhelming
            // majority here — every counter booking, every recurring occurrence and every
            // row that predates the column. So the index contains only the handful of live
            // unpaid holds, and the sweep never scans the largest table in the schema.
            e.HasIndex(b => new { b.PaymentDeadlineAt, b.Status });
        });

        modelBuilder.Entity<RecurringBookingGroup>(e =>
        {
            e.HasOne(g => g.Player).WithMany().HasForeignKey(g => g.PlayerId);
            e.HasOne(g => g.Venue).WithMany().HasForeignKey(g => g.VenueId);
        });

        modelBuilder.Entity<PermanentBooking>(e =>
        {
            e.HasOne(p => p.Venue).WithMany().HasForeignKey(p => p.VenueId);
            // Look-up index for the availability merge: (venue, weekday, status).
            e.HasIndex(p => new { p.VenueId, p.DayOfWeek, p.Status });
            e.HasIndex(p => p.CustomerId);
        });

        modelBuilder.Entity<Customer>(e =>
        {
            // The natural key. One phone is one customer per owner — this index is what
            // actually prevents the same person being recorded three times under three
            // spellings, and it is why PhoneNormalizer must run before every write.
            e.HasIndex(c => new { c.OwnerId, c.Phone }).IsUnique();

            // The list page: an owner's active book, newest first.
            e.HasIndex(c => new { c.OwnerId, c.Status });
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.HasOne(p => p.Booking)
             .WithMany()
             .HasForeignKey(p => p.BookingId);

            e.HasOne(p => p.Player)
             .WithMany()
             .HasForeignKey(p => p.PlayerId);

            // Both optional links detach rather than cascade. A payment must outlive the
            // customer record it points at and the staff account that recorded it: people
            // leave, accounts get archived, and none of that may erase the fact that money
            // changed hands.
            e.HasOne(p => p.Customer)
             .WithMany()
             .HasForeignKey(p => p.CustomerId)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(p => p.RecordedBy)
             .WithMany()
             .HasForeignKey(p => p.RecordedByUserId)
             .OnDelete(DeleteBehavior.SetNull);

            // The owner's ledger read: their venues, newest first.
            e.HasIndex(p => new { p.VenueId, p.Date });
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.HasOne(n => n.User).WithMany().HasForeignKey(n => n.UserId);
        });

        modelBuilder.Entity<DeviceToken>(e =>
        {
            e.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId);
            e.HasIndex(d => new { d.UserId, d.Token }).IsUnique();
        });

        modelBuilder.Entity<Favorite>(e =>
        {
            e.HasOne(f => f.User).WithMany().HasForeignKey(f => f.UserId);
            e.HasOne(f => f.Venue).WithMany().HasForeignKey(f => f.VenueId);
            e.HasIndex(f => new { f.UserId, f.VenueId }).IsUnique();
        });

        modelBuilder.Entity<Review>(e =>
        {
            e.HasOne(r => r.Player).WithMany().HasForeignKey(r => r.PlayerId);
            e.HasOne(r => r.Venue).WithMany().HasForeignKey(r => r.VenueId);
            e.HasIndex(r => new { r.PlayerId, r.VenueId }).IsUnique();
        });
    }

    // -----------------------------------------------------------------------------------
    // Append-only enforcement for the payment ledger
    // -----------------------------------------------------------------------------------

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardPaymentLedger();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardPaymentLedger();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// A payment row may be written once and never touched again.
    ///
    /// This lives in the DbContext rather than in a code-review convention because a rule
    /// that depends on everyone remembering is not a rule. Any future endpoint that loads a
    /// Payment, edits an amount and saves — however well-intentioned, "just fixing a typo in
    /// the note" — fails here, loudly, in development, with an explanation. That is the
    /// difference between a ledger and a table that happens to contain payments.
    ///
    /// Corrections have a correct shape: write a NEW row. The history of a mistake is part
    /// of the record, and quietly editing the past is precisely what an audit trail exists
    /// to make impossible.
    /// </summary>
    private void GuardPaymentLedger()
    {
        foreach (var entry in ChangeTracker.Entries<Payment>())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;

            // Verified by removal: neutering this throw fails ARecordedPaymentCannotBeEdited
            // and ARecordedPaymentCannotBeDeleted. A test that passes with and without the
            // mechanism it is named after proves nothing.
            var id = entry.Entity.Id;
            throw new InvalidOperationException(
                $"payments is an append-only ledger; attempted to {entry.State.ToString().ToLowerInvariant()} " +
                $"row '{id}'. To correct a recorded payment, add a new row describing the correction — " +
                "never rewrite history. See PaymentLedger.Settle.");
        }
    }
}
