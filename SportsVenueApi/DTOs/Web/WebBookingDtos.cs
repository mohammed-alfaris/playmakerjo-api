using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using SportsVenueApi.DTOs.Venues;

namespace SportsVenueApi.DTOs.Web;

/// <summary>What the venue's public booking page needs to draw itself. Never the CliQ alias.</summary>
public class WebVenueResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("nameAr")] public string? NameAr { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("cityAr")] public string? CityAr { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("addressAr")] public string? AddressAr { get; set; }
    [JsonPropertyName("latitude")] public double? Latitude { get; set; }
    [JsonPropertyName("longitude")] public double? Longitude { get; set; }
    [JsonPropertyName("images")] public List<string> Images { get; set; } = [];
    [JsonPropertyName("sports")] public List<string> Sports { get; set; } = [];
    [JsonPropertyName("pitches")] public List<PitchDto> Pitches { get; set; } = [];
    [JsonPropertyName("pricePerHour")] public double PricePerHour { get; set; }
    [JsonPropertyName("minDuration")] public int MinDuration { get; set; }
    [JsonPropertyName("maxDuration")] public int MaxDuration { get; set; }
    [JsonPropertyName("depositPercentage")] public double DepositPercentage { get; set; }
    [JsonPropertyName("freeCancelHours")] public int FreeCancelHours { get; set; }
    /// <summary>The venue can take a CliQ deposit (it has an alias and asks for a deposit).</summary>
    [JsonPropertyName("canPayNow")] public bool CanPayNow { get; set; }
    /// <summary>False while the venue is inactive or its company suspended: show, but take nothing.</summary>
    [JsonPropertyName("acceptingBookings")] public bool AcceptingBookings { get; set; }
    /// <summary>The venue's contact number (its owner's), for the guest to call.</summary>
    [JsonPropertyName("phone")] public string? Phone { get; set; }
}

public class WebBookingRequest
{
    [JsonPropertyName("sport")] [Required] public string Sport { get; set; } = "";
    [JsonPropertyName("pitchId")] public string? PitchId { get; set; }
    [JsonPropertyName("pitchSize")] public string? PitchSize { get; set; }
    [JsonPropertyName("date")] [Required] public string Date { get; set; } = "";
    [JsonPropertyName("startTime")] [Required] public string StartTime { get; set; } = "";
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("name")] [Required] [MaxLength(80)] public string Name { get; set; } = "";
    [JsonPropertyName("phone")] [Required] [MaxLength(32)] public string Phone { get; set; } = "";
    [JsonPropertyName("notes")] [MaxLength(300)] public string? Notes { get; set; }
    /// <summary>The guest chose to pay the CliQ deposit now and hold the slot until it is reviewed.</summary>
    [JsonPropertyName("payNow")] public bool PayNow { get; set; }
    /// <summary>Honeypot: a field people never see. Anything in it is a bot.</summary>
    [JsonPropertyName("website")] public string? Website { get; set; }
}

public class WebBookingCreated
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

/// <summary>The guest's status page. Built only from the booking their token names.</summary>
public class WebBookingStatus
{
    /// <summary>requested | awaiting_payment | awaiting_review | confirmed | completed | no_show | cancelled | expired</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("venueName")] public string VenueName { get; set; } = "";
    [JsonPropertyName("venueNameAr")] public string? VenueNameAr { get; set; }
    [JsonPropertyName("venueSlug")] public string? VenueSlug { get; set; }
    [JsonPropertyName("venuePhone")] public string? VenuePhone { get; set; }
    [JsonPropertyName("customerName")] public string? CustomerName { get; set; }
    [JsonPropertyName("sport")] public string? Sport { get; set; }
    [JsonPropertyName("pitchName")] public string? PitchName { get; set; }
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("startTime")] public string? StartTime { get; set; }
    [JsonPropertyName("duration")] public int Duration { get; set; }
    [JsonPropertyName("total")] public double Total { get; set; }
    [JsonPropertyName("deposit")] public double Deposit { get; set; }
    [JsonPropertyName("paid")] public double Paid { get; set; }
    /// <summary>Only while the guest is due to pay: where to send the deposit.</summary>
    [JsonPropertyName("cliqAlias")] public string? CliqAlias { get; set; }
    [JsonPropertyName("deadline")] public DateTime? Deadline { get; set; }
    /// <summary>Why the venue rejected the last proof, when it did.</summary>
    [JsonPropertyName("proofNote")] public string? ProofNote { get; set; }
    [JsonPropertyName("canPayNow")] public bool CanPayNow { get; set; }
    [JsonPropertyName("canCancel")] public bool CanCancel { get; set; }
    [JsonPropertyName("freeCancelHours")] public int FreeCancelHours { get; set; }
}
