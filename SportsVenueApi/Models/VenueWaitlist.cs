using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SportsVenueApi.Models;

[Table("venue_waitlist")]
public class VenueWaitlist
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("contact_name")]
    [MaxLength(255)]
    public string ContactName { get; set; } = "";

    [Column("venue_name")]
    [MaxLength(255)]
    public string VenueName { get; set; } = "";

    [Column("city")]
    [MaxLength(100)]
    public string City { get; set; } = "";

    [Column("phone")]
    [MaxLength(30)]
    public string Phone { get; set; } = "";

    [Column("email")]
    [MaxLength(255)]
    public string Email { get; set; } = "";

    [Column("sports")]
    public string SportsJson { get; set; } = "[]";

    [NotMapped]
    [JsonIgnore]
    public List<string> Sports
    {
        get => JsonSerializer.Deserialize<List<string>>(SportsJson) ?? [];
        set => SportsJson = JsonSerializer.Serialize(value);
    }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Sales pipeline ──────────────────────────────────────────────────────
    // A sign-up from the website is where a sale starts. These track it to a paying company.

    /// <summary>"new" | "contacted" | "demo" | "trial" | "won" | "lost"</summary>
    [Column("status")]
    [MaxLength(12)]
    public string Status { get; set; } = "new";

    [Column("notes", TypeName = "text")]
    public string? Notes { get; set; }

    /// <summary>Amman calendar date to get back to them.</summary>
    [Column("next_follow_up_on")]
    public DateTime? NextFollowUpOn { get; set; }

    [Column("lost_reason")]
    [MaxLength(255)]
    public string? LostReason { get; set; }

    /// <summary>The venue owner this lead became, once won.</summary>
    [Column("converted_owner_id")]
    [MaxLength(32)]
    public string? ConvertedOwnerId { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
