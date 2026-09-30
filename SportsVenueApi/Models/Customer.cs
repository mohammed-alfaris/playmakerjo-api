using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// A person who books at a venue, as recorded by the venue side.
///
/// Distinct from <see cref="User"/> on purpose. A User is someone with a login; the vast
/// majority of a Jordanian pitch's customers will never install anything — they phone or
/// message the owner, who writes the booking down. Before this existed the owner had
/// nowhere to put them: a manual booking stored <c>PlayerId = the owner's own id</c> and the
/// customer's name was jammed into the free-text notes field as
/// <c>"[MANUAL] [football] Walk-in: خالد."</c> with no phone at all.
///
/// Owned by the venue_owner, NOT by a venue: an owner with two branches must see one Khalid,
/// not two. Keyed by phone, because that is the only identifier a walk-in reliably has.
/// </summary>
[Table("customers")]
public class Customer
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = "cus_" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The venue_owner whose book this customer belongs to.</summary>
    [Column("owner_id")]
    [MaxLength(32)]
    public string OwnerId { get; set; } = "";

    /// <summary>
    /// Canonical <c>+9627XXXXXXXX</c>, produced by <see cref="Helpers.PhoneNormalizer"/>.
    /// The natural key — unique per owner. Never store what the user typed.
    /// </summary>
    [Column("phone")]
    [MaxLength(20)]
    public string Phone { get; set; } = "";

    [Column("name")]
    [MaxLength(120)]
    public string Name { get; set; } = "";

    /// <summary>
    /// The notebook margin: "always fifteen minutes late", "Abu Ahmad's friend". Free text,
    /// owner-authored, never parsed.
    /// </summary>
    [Column("note")]
    [MaxLength(280)]
    public string? Note { get; set; }

    /// <summary>"active" | "archived". Archive rather than delete — bookings reference this row.</summary>
    [Column("status")]
    [MaxLength(20)]
    public string Status { get; set; } = "active";

    [Column("archived_at")]
    public DateTime? ArchivedAt { get; set; }

    /// <summary>Who first recorded them — the owner or one of their staff.</summary>
    [Column("created_by_user_id")]
    [MaxLength(32)]
    public string? CreatedByUserId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
