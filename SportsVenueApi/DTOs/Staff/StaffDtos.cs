using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Staff;

public class StaffRoleResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    /// <summary>How many staff hold this role — the reason a delete may be refused.</summary>
    [JsonPropertyName("staffCount")]
    public int StaffCount { get; set; }
}

public class StaffRoleRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("permissions")]
    public List<string>? Permissions { get; set; }
}

/// <summary>A permission as the dashboard lists it: the key and the group it sits under.</summary>
public class StaffPermissionInfo
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("group")]
    public string Group { get; set; } = "";
}

/// <summary>Assign a staff member's role and venues. Each field is optional.</summary>
public class StaffAssignmentRequest
{
    [JsonPropertyName("staffRoleId")]
    public string? StaffRoleId { get; set; }

    [JsonPropertyName("allVenues")]
    public bool? AllVenues { get; set; }

    [JsonPropertyName("venueIds")]
    public List<string>? VenueIds { get; set; }
}

/// <summary>What the signed-in user may do in the back office — for gating the dashboard UI.</summary>
public class AccessSummary
{
    [JsonPropertyName("companyId")]
    public string? CompanyId { get; set; }

    [JsonPropertyName("companyName")]
    public string? CompanyName { get; set; }

    [JsonPropertyName("staffRole")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StaffRoleRef? StaffRole { get; set; }

    [JsonPropertyName("permissions")]
    public List<string> Permissions { get; set; } = [];

    /// <summary>True for owners, admins, and staff not limited to particular venues.</summary>
    [JsonPropertyName("allVenues")]
    public bool AllVenues { get; set; }

    [JsonPropertyName("venueIds")]
    public List<string> VenueIds { get; set; } = [];

    /// <summary>
    /// PlayMaker has suspended the caller's company: the back office is closed (the owner can
    /// still see billing). The dashboard shows a notice instead of empty pages.
    /// </summary>
    [JsonPropertyName("companySuspended")]
    public bool CompanySuspended { get; set; }
}

public class StaffRoleRef
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}
