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
}
