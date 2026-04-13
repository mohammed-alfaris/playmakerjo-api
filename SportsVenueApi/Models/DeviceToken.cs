using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("device_tokens")]
public class DeviceToken
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [Column("user_id")]
    [MaxLength(32)]
    public string UserId { get; set; } = "";

    [Column("token")]
    [MaxLength(500)]
    public string Token { get; set; } = "";

    [Column("platform")]
    [MaxLength(20)]
    public string Platform { get; set; } = "";  // "android", "ios", "web"

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("UserId")]
    public User User { get; set; } = null!;
}
