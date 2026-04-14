using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

[Table("notification_templates")]
public class NotificationTemplate
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [Column("name")]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    [Column("title")]
    [MaxLength(255)]
    public string Title { get; set; } = "";

    [Column("body", TypeName = "text")]
    public string Body { get; set; } = "";

    [Column("type")]
    [MaxLength(50)]
    public string Type { get; set; } = "general";  // general, update, promo, maintenance

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
