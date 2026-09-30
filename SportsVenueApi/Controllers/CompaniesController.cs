using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Billing;
using SportsVenueApi.DTOs.Companies;
using SportsVenueApi.DTOs.Leads;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

/// <summary>
/// Owners' companies: the owner sees their own name and usage; super_admin sees every company
/// and sets its limits.
/// </summary>
[ApiController]
[Authorize]
public class CompaniesController : ControllerBase
{
    private const int MaxNameLength = 120;

    private readonly AppDbContext _db;
    private readonly AccessContext _access;
    private readonly CompanyService _companies;
    private readonly BillingService _billing;

    public CompaniesController(AppDbContext db, AccessContext access, CompanyService companies, BillingService billing)
    {
        _db = db;
        _access = access;
        _companies = companies;
        _billing = billing;
    }

    /// <summary>GET /api/v1/companies/me — the owner's company, with usage against limits.</summary>
    [HttpGet("api/v1/companies/me")]
    public async Task<IActionResult> Mine()
    {
        // By the owner's own id: a suspended owner has no back office but still needs to see
        // why, and what is owed.
        if (!_access.IsOwner) return Forbid();
        var company = await _companies.EnsureAsync(_access.UserId);
        return Ok(new ApiResponse<CompanyResponse> { Data = await ToDtoAsync(company) });
    }

    /// <summary>
    /// PATCH /api/v1/companies/me — the owner may rename their company. Limits are the
    /// platform's to set, so they are ignored here.
    /// </summary>
    [HttpPatch("api/v1/companies/me")]
    public async Task<IActionResult> UpdateMine([FromBody] UpdateCompanyRequest req)
    {
        if (!_access.IsOwner || _access.CompanyId == null) return Forbid();
        if (req.Limits != null)
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Limits are set by PlayMaker." });

        var company = await _companies.EnsureAsync(_access.CompanyId);
        if (ApplyNames(company, req) is { } invalid)
            return BadRequest(new ApiResponse<object> { Success = false, Message = invalid });

        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<CompanyResponse> { Data = await ToDtoAsync(company), Message = "Company updated" });
    }

    /// <summary>GET /api/v1/companies — every company, admin only. One per venue owner.</summary>
    [HttpGet("api/v1/companies")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int limit = 20, [FromQuery] string? search = null)
    {
        if (page < 1) page = 1;
        if (limit is < 1 or > 100) limit = 20;

        // Listed from the owners, not from the companies table, so an owner whose company has
        // not been created yet still appears — and gets one as they are listed.
        var owners = _db.Users.AsNoTracking().Where(u => u.Role == "venue_owner");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            // The company's own names too: once renamed, that is what an admin remembers it by.
            owners = owners.Where(u =>
                EF.Functions.Like(u.Name, $"%{term}%")
                || EF.Functions.Like(u.Email, $"%{term}%")
                || _db.Companies.Any(c => c.OwnerId == u.Id
                    && (EF.Functions.Like(c.Name, $"%{term}%") || EF.Functions.Like(c.NameAr!, $"%{term}%"))));
        }

        var total = await owners.CountAsync();
        var pageIds = await owners.OrderBy(u => u.Name).Skip((page - 1) * limit).Take(limit)
            .Select(u => u.Id).ToListAsync();

        var data = new List<CompanyResponse>();
        foreach (var id in pageIds)
            data.Add(await ToDtoAsync(await _companies.EnsureAsync(id)));

        return Ok(new ApiResponse<List<CompanyResponse>>
        {
            Data = data,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    [HttpGet("api/v1/companies/{ownerId}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Get(string ownerId)
    {
        if (!await IsOwnerAsync(ownerId))
            return NotFound(new ApiResponse<object> { Success = false, Message = "Company not found" });
        return Ok(new ApiResponse<CompanyResponse> { Data = await ToDtoAsync(await _companies.EnsureAsync(ownerId)) });
    }

    /// <summary>
    /// PATCH /api/v1/companies/{ownerId} — admin sets names and limits. Lowering a limit below
    /// what the company already has is allowed and switches nothing off: it only stops new
    /// venues or staff until usage drops under it.
    /// </summary>
    [HttpPatch("api/v1/companies/{ownerId}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Update(string ownerId, [FromBody] UpdateCompanyRequest req)
    {
        if (!await IsOwnerAsync(ownerId))
            return NotFound(new ApiResponse<object> { Success = false, Message = "Company not found" });

        var company = await _companies.EnsureAsync(ownerId);
        if (ApplyNames(company, req) is { } invalid)
            return BadRequest(new ApiResponse<object> { Success = false, Message = invalid });

        if (req.Limits != null)
        {
            if (req.Limits.MaxVenues < 0 || req.Limits.MaxStaff < 0)
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Limits cannot be negative." });
            company.MaxVenues = req.Limits.MaxVenues;
            company.MaxStaff = req.Limits.MaxStaff;
        }

        company.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<CompanyResponse> { Data = await ToDtoAsync(company), Message = "Company updated" });
    }

    /// <summary>GET /api/v1/companies/me/onboarding — the owner's set-up checklist.</summary>
    [HttpGet("api/v1/companies/me/onboarding")]
    public async Task<IActionResult> MyOnboarding()
    {
        if (!_access.IsOwner || _access.CompanyId == null) return Forbid();
        return Ok(new ApiResponse<OnboardingResponse> { Data = await OnboardingOfAsync(_access.CompanyId) });
    }

    /// <summary>GET /api/v1/companies/{ownerId}/onboarding — admin checks how a trial is going.</summary>
    [HttpGet("api/v1/companies/{ownerId}/onboarding")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Onboarding(string ownerId)
    {
        if (!await IsOwnerAsync(ownerId))
            return NotFound(new ApiResponse<object> { Success = false, Message = "Company not found" });
        return Ok(new ApiResponse<OnboardingResponse> { Data = await OnboardingOfAsync(ownerId) });
    }

    private async Task<OnboardingResponse> OnboardingOfAsync(string ownerId)
    {
        var steps = await _companies.OnboardingAsync(ownerId);
        return new OnboardingResponse
        {
            Steps = steps.Select(s => new OnboardingStep { Key = s.Key, Done = s.Done }).ToList(),
            Done = steps.Count(s => s.Done),
            Total = steps.Count,
        };
    }

    /// <summary>
    /// PATCH /api/v1/companies/{ownerId}/billing — admin sets the cycle, trial end, prices and
    /// setup-fee waiver. Takes effect from the next invoice drafted; drafts already made keep
    /// their lines (void and generate again to re-price one).
    /// </summary>
    [HttpPatch("api/v1/companies/{ownerId}/billing")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> UpdateBilling(string ownerId, [FromBody] UpdateCompanyBillingRequest req)
    {
        if (!await IsOwnerAsync(ownerId))
            return NotFound(new ApiResponse<object> { Success = false, Message = "Company not found" });
        var company = await _companies.EnsureAsync(ownerId);

        if (req.Cycle != null)
        {
            if (req.Cycle is not (BillingService.Monthly or BillingService.Annual))
                return BadRequest(new ApiResponse<object> { Success = false, Message = "cycle must be monthly or annual" });
            company.BillingCycle = req.Cycle;
        }
        if (req.TrialEndsOn != null)
        {
            if (req.TrialEndsOn.Length == 0) company.TrialEndsOn = null;
            else if (DateTime.TryParseExact(req.TrialEndsOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                company.TrialEndsOn = d.Date;
            else
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Use yyyy-MM-dd for the trial end" });
        }
        if (req.Prices != null)
        {
            company.PriceFirstVenue = req.Prices.FirstVenue;
            company.PriceExtraVenue = req.Prices.ExtraVenue;
        }
        if (req.SetupFeeWaived is { } waived) company.SetupFeeWaived = waived;

        company.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<CompanyResponse> { Data = await ToDtoAsync(company), Message = "Billing updated" });
    }

    /// <summary>
    /// PATCH /api/v1/companies/{ownerId}/suspension — admin stops (or restarts) a company. While
    /// suspended its owner and staff keep their sign-in but lose the back office, and its
    /// venues leave the app. Nothing is deleted; lifting it restores everything as it was.
    /// </summary>
    [HttpPatch("api/v1/companies/{ownerId}/suspension")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Suspend(string ownerId, [FromBody] SuspendCompanyRequest req)
    {
        if (!await IsOwnerAsync(ownerId))
            return NotFound(new ApiResponse<object> { Success = false, Message = "Company not found" });
        var company = await _companies.EnsureAsync(ownerId);

        if (req.Suspended)
        {
            company.SuspendedAt ??= DateTime.UtcNow;
            company.SuspendedReason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        }
        else
        {
            company.SuspendedAt = null;
            company.SuspendedReason = null;
        }

        company.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<CompanyResponse>
        {
            Data = await ToDtoAsync(company),
            Message = req.Suspended ? "Company suspended" : "Company restored",
        });
    }

    // ------------------------------------------------------------------------------------

    private Task<bool> IsOwnerAsync(string id) =>
        _db.Users.AnyAsync(u => u.Id == id && u.Role == "venue_owner");

    private static string? ApplyNames(Company company, UpdateCompanyRequest req)
    {
        if (req.Name != null)
        {
            var name = req.Name.Trim();
            if (name.Length == 0) return "The company needs a name.";
            if (name.Length > MaxNameLength) return $"Names can be at most {MaxNameLength} characters.";
            company.Name = name;
        }
        if (req.NameAr != null)
        {
            var nameAr = req.NameAr.Trim();
            if (nameAr.Length > MaxNameLength) return $"Names can be at most {MaxNameLength} characters.";
            company.NameAr = nameAr.Length == 0 ? null : nameAr;
        }
        return null;
    }

    private async Task<CompanyResponse> ToDtoAsync(Company company)
    {
        var owner = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == company.OwnerId);
        var usage = await _companies.UsageAsync(company);
        return new CompanyResponse
        {
            Id = company.OwnerId,
            Name = company.Name,
            NameAr = company.NameAr,
            OwnerName = owner.Name,
            OwnerEmail = owner.Email,
            OwnerStatus = owner.Status,
            Venues = new UsageInfo { Used = usage.Venues, Max = usage.MaxVenues },
            Staff = new UsageInfo { Used = usage.Staff, Max = usage.MaxStaff },
            CreatedAt = company.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Billing = await BillingOfAsync(company),
        };
    }

    private async Task<CompanyBilling> BillingOfAsync(Company company)
    {
        var (first, extra) = await _billing.PricesForAsync(company);
        var (count, amount) = await _billing.OverdueAsync(company.OwnerId);
        return new CompanyBilling
        {
            Status = BillingService.StatusOf(company, PlatformConstants.JordanToday()),
            Cycle = company.BillingCycle,
            TrialEndsOn = company.TrialEndsOn?.ToString("yyyy-MM-dd"),
            PriceFirstVenue = first,
            PriceExtraVenue = extra,
            CustomPrices = company.PriceFirstVenue != null || company.PriceExtraVenue != null,
            SetupFeeWaived = company.SetupFeeWaived,
            OverdueCount = count,
            OverdueAmount = amount,
            SuspendedAt = company.SuspendedAt,
            SuspendedReason = company.SuspendedReason,
        };
    }
}
