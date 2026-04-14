using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("users")]
public class User
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = "";

    [Column("email")]
    [MaxLength(255)]
    public string Email { get; set; } = "";

    [Column("phone")]
    [MaxLength(50)]
    public string? Phone { get; set; }

    [Column("password_hash")]
    [MaxLength(255)]
    public string PasswordHash { get; set; } = "";

    [Column("role")]
    [MaxLength(50)]
    public string Role { get; set; } = "player";

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "active";

    [Column("avatar", TypeName = "text")]
    public string? Avatar { get; set; }

    /// <summary>"read" | "write" — only relevant for venue_staff</summary>
    [Column("permissions")]
    [MaxLength(20)]
    public string? Permissions { get; set; }

    /// <summary>"en" or "ar" — used for push notification language</summary>
    [Column("preferred_language")]
    [MaxLength(5)]
    public string PreferredLanguage { get; set; } = "en";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
