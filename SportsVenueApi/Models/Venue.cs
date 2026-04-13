using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("venues")]
public class Venue
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = "";

    [Column("owner_id")]
    [MaxLength(32)]
    public string OwnerId { get; set; } = "";

    [Column("sports", TypeName = "longtext")]
    public string SportsJson { get; set; } = "[]";

    [Column("city")]
    [MaxLength(255)]
    public string? City { get; set; }

    [Column("address")]
    [MaxLength(500)]
    public string? Address { get; set; }

    [Column("price_per_hour")]
    public double PricePerHour { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "pending";

    [Column("description", TypeName = "text")]
    public string? Description { get; set; }

    [Column("images", TypeName = "json")]
    public string ImagesJson { get; set; } = "[]";

    [Column("latitude")]
    public double? Latitude { get; set; }

    [Column("longitude")]
    public double? Longitude { get; set; }

    [Column("cliq_alias")]
    [MaxLength(255)]
    public string? CliqAlias { get; set; }

    [Column("operating_hours", TypeName = "json")]
    public string OperatingHoursJson { get; set; } = "{}";

    [Column("min_booking_duration")]
    public int MinBookingDuration { get; set; } = 1;

    [Column("max_booking_duration")]
    public int MaxBookingDuration { get; set; } = 3;

    [Column("deposit_percentage")]
    public double DepositPercentage { get; set; } = 20.0;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("OwnerId")]
    public User Owner { get; set; } = null!;

    [NotMapped]
    public List<string> Sports
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(SportsJson) ?? [];
        set => SportsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public List<string> Images
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(ImagesJson) ?? [];
        set => ImagesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public Dictionary<string, object>? OperatingHours
    {
        get => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(OperatingHoursJson);
        set => OperatingHoursJson = System.Text.Json.JsonSerializer.Serialize(value ?? new Dictionary<string, object>());
    }
}
