using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Reviews;

public class ReviewResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("playerId")]
    public string PlayerId { get; set; } = "";

    [JsonPropertyName("playerName")]
    public string PlayerName { get; set; } = "";

    [JsonPropertyName("playerAvatar")]
    public string? PlayerAvatar { get; set; }

    [JsonPropertyName("venueId")]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("rating")]
    public int Rating { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = "";
}

public class CreateReviewRequest
{
    [JsonPropertyName("venueId")]
    [Required]
    public string VenueId { get; set; } = "";

    [JsonPropertyName("rating")]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5")]
    public int Rating { get; set; }

    [JsonPropertyName("comment")]
    [MaxLength(500, ErrorMessage = "Comment must be 500 characters or fewer")]
    public string? Comment { get; set; }
}

public class UpdateReviewRequest
{
    [JsonPropertyName("rating")]
    [Range(1, 5, ErrorMessage = "Rating must be between 1 and 5")]
    public int Rating { get; set; }

    [JsonPropertyName("comment")]
    [MaxLength(500, ErrorMessage = "Comment must be 500 characters or fewer")]
    public string? Comment { get; set; }
}

public class ReviewEligibilityResponse
{
    [JsonPropertyName("canReview")]
    public bool CanReview { get; set; }

    [JsonPropertyName("hasExistingReview")]
    public bool HasExistingReview { get; set; }
}
