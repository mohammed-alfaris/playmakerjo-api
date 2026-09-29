using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// A venue owner's business, as the CRM sees it: its name and the limits the platform sets on it.
///
/// Keyed on the owner's user id, 1:1. The owner remains the tenant everywhere else —
/// Venue.OwnerId, Customer.OwnerId and User.ManagedByOwnerId all still point at that user —
/// so this table adds to the model without rewriting a single existing key.
/// </summary>
[Table("companies")]
public class Company
{
    [Key]
    [Column("owner_id")]
    [MaxLength(32)]
    public string OwnerId { get; set; } = "";

    [Column("name")]
    [MaxLength(120)]
    public string Name { get; set; } = "";

    [Column("name_ar")]
    [MaxLength(120)]
    public string? NameAr { get; set; }

    /// <summary>How many venues the company may have. Null means no limit.</summary>
    [Column("max_venues")]
    public int? MaxVenues { get; set; }

    /// <summary>How many ACTIVE staff the company may have. Null means no limit.</summary>
    [Column("max_staff")]
    public int? MaxStaff { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(OwnerId))]
    public User Owner { get; set; } = null!;
}
