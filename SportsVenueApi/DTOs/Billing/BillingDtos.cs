using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Billing;

public class InvoiceLineResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>"subscription" | "extra_venues" | "setup_fee" | "commission" | "adjustment"</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("descriptionAr")] public string? DescriptionAr { get; set; }
    [JsonPropertyName("quantity")] public double Quantity { get; set; }
    [JsonPropertyName("unitPrice")] public double UnitPrice { get; set; }
    [JsonPropertyName("amount")] public double Amount { get; set; }
}

public class InvoiceResponse
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    /// <summary>Null while a draft.</summary>
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("ownerId")] public string OwnerId { get; set; } = "";
    [JsonPropertyName("companyName")] public string CompanyName { get; set; } = "";
    [JsonPropertyName("companyNameAr")] public string? CompanyNameAr { get; set; }
    [JsonPropertyName("period")] public string Period { get; set; } = "";
    /// <summary>"draft" | "issued" | "paid" | "void"</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    /// <summary>Issued, unpaid and past its due date.</summary>
    [JsonPropertyName("overdue")] public bool Overdue { get; set; }
    [JsonPropertyName("total")] public double Total { get; set; }
    [JsonPropertyName("issuedAt")] public DateTime? IssuedAt { get; set; }
    [JsonPropertyName("dueOn")] public string? DueOn { get; set; }
    [JsonPropertyName("paidAt")] public DateTime? PaidAt { get; set; }
    [JsonPropertyName("paidMethod")] public string? PaidMethod { get; set; }
    [JsonPropertyName("paidReference")] public string? PaidReference { get; set; }
    [JsonPropertyName("voidReason")] public string? VoidReason { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("lines")] public List<InvoiceLineResponse> Lines { get; set; } = [];
}

public class GenerateInvoicesRequest
{
    /// <summary>"yyyy-MM"</summary>
    [JsonPropertyName("period")] [Required] public string Period { get; set; } = "";
    /// <summary>Only this company. Omitted = every company.</summary>
    [JsonPropertyName("ownerId")] public string? OwnerId { get; set; }
}

public class SkippedCompany
{
    [JsonPropertyName("ownerId")] public string OwnerId { get; set; } = "";
    [JsonPropertyName("companyName")] public string CompanyName { get; set; } = "";
    /// <summary>"already_invoiced" | "in_trial" | "nothing_to_bill" | "suspended" | "owner_inactive"</summary>
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
}

public class GenerateInvoicesResponse
{
    [JsonPropertyName("period")] public string Period { get; set; } = "";
    [JsonPropertyName("created")] public List<InvoiceResponse> Created { get; set; } = [];
    [JsonPropertyName("skipped")] public List<SkippedCompany> Skipped { get; set; } = [];
}

public class AddInvoiceLineRequest
{
    [JsonPropertyName("description")] [Required] [StringLength(255, MinimumLength = 1)] public string Description { get; set; } = "";
    [JsonPropertyName("descriptionAr")] [StringLength(255)] public string? DescriptionAr { get; set; }
    /// <summary>Negative for a discount.</summary>
    [JsonPropertyName("amount")] [Range(-100000, 100000)] public double Amount { get; set; }
}

public class PayInvoiceRequest
{
    /// <summary>"cliq" | "bank_transfer" | "cash"</summary>
    [JsonPropertyName("method")] [Required] public string Method { get; set; } = "";
    [JsonPropertyName("reference")] [StringLength(100)] public string? Reference { get; set; }
}

public class VoidInvoiceRequest
{
    [JsonPropertyName("reason")] [StringLength(255)] public string? Reason { get; set; }
}

/// <summary>Where a company stands with PlayMaker. Prices are what it pays (its own or the default).</summary>
public class CompanyBilling
{
    /// <summary>"trial" | "active" | "suspended"</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    /// <summary>"monthly" | "annual"</summary>
    [JsonPropertyName("cycle")] public string Cycle { get; set; } = "";
    [JsonPropertyName("trialEndsOn")] public string? TrialEndsOn { get; set; }
    [JsonPropertyName("priceSmallVenue")] public double PriceSmallVenue { get; set; }
    [JsonPropertyName("priceLargeVenue")] public double PriceLargeVenue { get; set; }
    /// <summary>Pitches from which a venue is "large" (platform-wide).</summary>
    [JsonPropertyName("largeVenueMinPitches")] public int LargeVenueMinPitches { get; set; }
    /// <summary>True when the price is this company's own rather than the platform default.</summary>
    [JsonPropertyName("customPrices")] public bool CustomPrices { get; set; }
    [JsonPropertyName("setupFeeWaived")] public bool SetupFeeWaived { get; set; }
    [JsonPropertyName("overdueCount")] public int OverdueCount { get; set; }
    [JsonPropertyName("overdueAmount")] public double OverdueAmount { get; set; }
    [JsonPropertyName("suspendedAt")] public DateTime? SuspendedAt { get; set; }
    [JsonPropertyName("suspendedReason")] public string? SuspendedReason { get; set; }
}

public class UpdateCompanyBillingRequest
{
    [JsonPropertyName("cycle")] public string? Cycle { get; set; }
    /// <summary>"yyyy-MM-dd"; empty string ends the trial (no free days left).</summary>
    [JsonPropertyName("trialEndsOn")] public string? TrialEndsOn { get; set; }
    /// <summary>When present, BOTH prices are set from it; a null price means "use the default".</summary>
    [JsonPropertyName("prices")] public CompanyPricesRequest? Prices { get; set; }
    [JsonPropertyName("setupFeeWaived")] public bool? SetupFeeWaived { get; set; }
}

public class CompanyPricesRequest
{
    [JsonPropertyName("smallVenue")] [Range(0, 10000)] public double? SmallVenue { get; set; }
    [JsonPropertyName("largeVenue")] [Range(0, 10000)] public double? LargeVenue { get; set; }
}

public class SuspendCompanyRequest
{
    [JsonPropertyName("suspended")] public bool Suspended { get; set; }
    [JsonPropertyName("reason")] [StringLength(255)] public string? Reason { get; set; }
}
