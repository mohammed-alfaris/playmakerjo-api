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

    /// <summary>
    /// For <c>venue_staff</c> only: the venue_owner this account works for. Staff inherit
    /// access to that owner's venues, gated by <see cref="Permissions"/>.
    ///
    /// Until this existed a staff account belonged to nobody — the role was created but
    /// never linked to anything, so the server could not answer "which venue does this
    /// user work for". Every staff authorization decision was therefore either "block all
    /// staff" or, in the deny-list guards, "let all staff through unscoped".
    ///
    /// Null for every other role, and null for legacy staff rows created before this
    /// column existed — which is why the access rules must treat null as "no access"
    /// rather than "all access".
    /// </summary>
    [Column("managed_by_owner_id")]
    [MaxLength(32)]
    public string? ManagedByOwnerId { get; set; }

    /// <summary>"en" or "ar" — used for push notification language</summary>
    [Column("preferred_language")]
    [MaxLength(5)]
    public string PreferredLanguage { get; set; } = "en";

    /// <summary>
    /// When this account's password last changed. Null means "never since this column
    /// existed", which is every row at the time it was added.
    ///
    /// This is the ONLY session-revocation lever for a password change. Refresh tokens live
    /// seven days and <c>POST /auth/refresh</c> validates only signature, expiry and
    /// <c>Status != "banned"</c> — it never looks at the password. So without this column a
    /// reset changed nothing for anyone already holding a refresh cookie: they kept minting
    /// fresh access tokens for a week, which makes "reset their password" security theatre
    /// in exactly the situation you would reset it.
    ///
    /// Refresh compares the token's <c>iat</c> against this and refuses anything older. The
    /// ≤15-minute access-token window stays open, the same way it does for suspension.
    /// </summary>
    [Column("password_changed_at")]
    public DateTime? PasswordChangedAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
