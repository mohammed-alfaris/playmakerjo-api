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
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.HasOne(p => p.Booking)
             .WithMany()
             .HasForeignKey(p => p.BookingId);

            e.HasOne(p => p.Player)
             .WithMany()
             .HasForeignKey(p => p.PlayerId);
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
}
