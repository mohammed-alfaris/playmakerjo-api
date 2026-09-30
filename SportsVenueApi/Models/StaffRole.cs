using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// A role an owner defines for their own staff: a name and a set of permission keys from
/// <see cref="Constants.StaffPermissions"/>. Roles belong to one company and are never shared.
/// </summary>
[Table("staff_roles")]
public class StaffRole
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = "sr_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>The company (owner user id) this role belongs to.</summary>
    [Column("owner_id")]
    [MaxLength(32)]
    public string OwnerId { get; set; } = "";

    [Column("name")]
    [MaxLength(60)]
    public string Name { get; set; } = "";

    [Column("permissions", TypeName = "longtext")]
    public string PermissionsJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Builds a new list on each read: assign it back after changing it.
    [NotMapped]
    public List<string> Permissions
    {
        get => string.IsNullOrWhiteSpace(PermissionsJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(PermissionsJson) ?? [];
        set => PermissionsJson = System.Text.Json.JsonSerializer.Serialize(value ?? []);
    }
}
