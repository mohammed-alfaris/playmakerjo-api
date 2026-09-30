using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Leads;

public class VenueLeadResponse
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("contactName")] public string ContactName { get; set; } = "";
    [JsonPropertyName("venueName")] public string VenueName { get; set; } = "";
    [JsonPropertyName("city")] public string City { get; set; } = "";
    [JsonPropertyName("phone")] public string Phone { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    /// <summary>Kept for the older leads table, which parses it.</summary>
    [JsonPropertyName("sportsJson")] public string SportsJson { get; set; } = "[]";
    [JsonPropertyName("sports")] public List<string> Sports { get; set; } = [];
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    /// <summary>"new" | "contacted" | "demo" | "trial" | "won" | "lost"</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("nextFollowUpOn")] public string? NextFollowUpOn { get; set; }
    /// <summary>The follow-up date has come (today or earlier) on a lead still in play.</summary>
    [JsonPropertyName("followUpDue")] public bool FollowUpDue { get; set; }
    [JsonPropertyName("lostReason")] public string? LostReason { get; set; }
    [JsonPropertyName("convertedOwnerId")] public string? ConvertedOwnerId { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime? UpdatedAt { get; set; }
}

public class UpdateVenueLeadRequest
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    /// <summary>Replaces the notes; empty string clears them.</summary>
    [JsonPropertyName("notes")] [StringLength(4000)] public string? Notes { get; set; }
    /// <summary>"yyyy-MM-dd"; empty string clears it.</summary>
    [JsonPropertyName("nextFollowUpOn")] public string? NextFollowUpOn { get; set; }
    [JsonPropertyName("lostReason")] [StringLength(255)] public string? LostReason { get; set; }
    /// <summary>The owner account this lead became. Setting it marks the lead won.</summary>
    [JsonPropertyName("convertedOwnerId")] public string? ConvertedOwnerId { get; set; }
}

public class LeadStats
{
    [JsonPropertyName("byStatus")] public Dictionary<string, int> ByStatus { get; set; } = [];
    [JsonPropertyName("followUpsDue")] public int FollowUpsDue { get; set; }
}

public class OnboardingStep
{
    /// <summary>"venue" | "pitches" | "hours" | "cliq" | "staff" | "first_booking" | "customer" | "standing"</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("done")] public bool Done { get; set; }
}

public class OnboardingResponse
{
    [JsonPropertyName("steps")] public List<OnboardingStep> Steps { get; set; } = [];
    [JsonPropertyName("done")] public int Done { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
}
