using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// One thing someone did in a back office: a booking cancelled, money refunded, a price
/// changed, a clerk added. Append-only — nothing updates or deletes these.
///
/// The summary is written at the time, in both languages ("english|arabic", like
/// notifications), with the names as they were then — so the log still reads right after a
/// customer is renamed or a clerk leaves.
/// </summary>
[Table("audit_events")]
public class AuditEvent
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("at")]
    public DateTime At { get; set; } = DateTime.UtcNow;

    /// <summary>The company it happened in; null for platform-wide changes (settings).</summary>
    [Column("owner_id")]
    [MaxLength(32)]
    public string? OwnerId { get; set; }

    [Column("actor_user_id")]
    [MaxLength(32)]
    public string? ActorUserId { get; set; }

    [Column("actor_name")]
    [MaxLength(120)]
    public string? ActorName { get; set; }

    [Column("actor_role")]
    [MaxLength(20)]
    public string? ActorRole { get; set; }

    /// <summary>"booking.cancelled", "payment.refund", "venue.updated", …</summary>
    [Column("action")]
    [MaxLength(40)]
    public string Action { get; set; } = "";

    [Column("entity_type")]
    [MaxLength(20)]
    public string EntityType { get; set; } = "";

    [Column("entity_id")]
    [MaxLength(40)]
    public string? EntityId { get; set; }

    /// <summary>"english|arabic"</summary>
    [Column("summary", TypeName = "text")]
    public string Summary { get; set; } = "";
}
