using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Billing;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

/// <summary>
/// PlayMaker's invoices to companies. The admin drafts a month (Generate), adjusts, issues and
/// records payment; the owner sees their issued invoices — even while suspended, since paying
/// is how a suspension ends.
///
/// Routes:
///   GET    /api/v1/invoices?period=&amp;status=&amp;owner_id=     admin: all · owner: own, no drafts
///   GET    /api/v1/invoices/{id}
///   POST   /api/v1/invoices/generate {period}                admin
///   POST   /api/v1/invoices/{id}/lines · DELETE …/lines/{lineId}   admin, drafts only
///   POST   /api/v1/invoices/{id}/issue | pay | void          admin
/// </summary>
[ApiController]
[Authorize]
public class InvoicesController : ControllerBase
{
    private static readonly string[] PayMethods = ["cliq", "bank_transfer", "cash"];

    private readonly AppDbContext _db;
    private readonly AccessContext _access;
    private readonly BillingService _billing;
    private readonly NotificationService _notifications;
    private readonly ILogger<InvoicesController> _logger;
    private readonly AuditLog _audit;

    public InvoicesController(AppDbContext db, AccessContext access, BillingService billing,
        NotificationService notifications, ILogger<InvoicesController> logger, AuditLog audit)
    {
        _audit = audit;
        _db = db;
        _access = access;
        _billing = billing;
        _notifications = notifications;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";

    private IActionResult Fail(int status, string message) =>
        StatusCode(status, new ApiResponse<object> { Success = false, Message = message });

    [HttpGet("api/v1/invoices")]
    public async Task<IActionResult> List(
        [FromQuery] string? period = null, [FromQuery] string? status = null,
        [FromQuery(Name = "owner_id")] string? ownerId = null,
        [FromQuery] int page = 1, [FromQuery] int limit = 50)
    {
        if (!_access.IsAdmin && !_access.IsOwner) return Forbid();
        if (page < 1) page = 1;
        if (limit is < 1 or > 200) limit = 50;

        var q = _db.Invoices.AsNoTracking().AsQueryable();
        if (_access.IsOwner)
            // By the owner's own id, not the back-office company: a suspended owner still sees these.
            q = q.Where(i => i.OwnerId == _access.UserId && i.Status != "draft");
        else if (!string.IsNullOrEmpty(ownerId))
            q = q.Where(i => i.OwnerId == ownerId);

        if (!string.IsNullOrEmpty(period)) q = q.Where(i => i.Period == period);
        if (status == "overdue")
        {
            var today = PlatformConstants.JordanToday();
            q = q.Where(i => i.Status == "issued" && i.DueOn < today);
        }
        else if (!string.IsNullOrEmpty(status)) q = q.Where(i => i.Status == status);

        var total = await q.CountAsync();
        var rows = await q.Include(i => i.Lines)
            .OrderByDescending(i => i.Period).ThenBy(i => i.Number == null).ThenByDescending(i => i.Number)
            .Skip((page - 1) * limit).Take(limit)
            .AsSplitQuery()
            .ToListAsync();

        return Ok(new ApiResponse<List<InvoiceResponse>>
        {
            Data = await ToDtosAsync(rows),
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total },
        });
    }

    [HttpGet("api/v1/invoices/{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var invoice = await LoadAsync(id);
        if (invoice == null || !MaySee(invoice)) return Fail(404, "Invoice not found");
        return Ok(new ApiResponse<InvoiceResponse> { Data = (await ToDtosAsync([invoice]))[0] });
    }

    [HttpPost("api/v1/invoices/generate")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Generate([FromBody] GenerateInvoicesRequest req)
    {
        if (!BillingService.TryParsePeriod(req.Period, out var month))
            return Fail(400, "Use yyyy-MM for the month");

        if (!string.IsNullOrEmpty(req.OwnerId) && !await _db.Users.AnyAsync(u => u.Id == req.OwnerId && u.Role == "venue_owner"))
            return Fail(404, "Company not found");

        var result = await _billing.GenerateAsync(month, UserId, string.IsNullOrEmpty(req.OwnerId) ? null : req.OwnerId);
        _logger.LogInformation("Invoices generated for {Period}: {Created} drafted, {Skipped} skipped",
            req.Period, result.Created.Count, result.Skipped.Count);

        return Ok(new ApiResponse<GenerateInvoicesResponse>
        {
            Data = new GenerateInvoicesResponse
            {
                Period = BillingService.PeriodOf(month),
                Created = await ToDtosAsync(result.Created),
                Skipped = result.Skipped
                    .Select(s => new SkippedCompany { OwnerId = s.OwnerId, CompanyName = s.CompanyName, Reason = s.Reason })
                    .ToList(),
            },
            Message = $"{result.Created.Count} draft invoice(s) created",
        });
    }

    [HttpPost("api/v1/invoices/{id}/lines")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> AddLine(string id, [FromBody] AddInvoiceLineRequest req)
    {
        var invoice = await LoadAsync(id);
        if (invoice == null) return Fail(404, "Invoice not found");
        if (invoice.Status != "draft") return Fail(400, "Only a draft can be changed. Void it and generate again.");
        if (Math.Abs(req.Amount) < 0.0005) return Fail(400, "Enter an amount");

        invoice.Lines.Add(new InvoiceLine
        {
            Kind = "adjustment",
            Description = req.Description.Trim(),
            DescriptionAr = string.IsNullOrWhiteSpace(req.DescriptionAr) ? null : req.DescriptionAr.Trim(),
            Quantity = 1, UnitPrice = req.Amount, Amount = Math.Round(req.Amount, 3),
            Sort = invoice.Lines.Count == 0 ? 0 : invoice.Lines.Max(l => l.Sort) + 1,
        });
        BillingService.Recalculate(invoice);
        if (invoice.Total < 0) return Fail(400, "An invoice cannot total less than zero");
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<InvoiceResponse> { Data = (await ToDtosAsync([invoice]))[0], Message = "Line added" });
    }

    [HttpDelete("api/v1/invoices/{id}/lines/{lineId}")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> RemoveLine(string id, string lineId)
    {
        var invoice = await LoadAsync(id);
        if (invoice == null) return Fail(404, "Invoice not found");
        if (invoice.Status != "draft") return Fail(400, "Only a draft can be changed. Void it and generate again.");
        var line = invoice.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return Fail(404, "Line not found");

        invoice.Lines.Remove(line);
        _db.InvoiceLines.Remove(line);
        BillingService.Recalculate(invoice);
        if (invoice.Total < 0) return Fail(400, "An invoice cannot total less than zero");
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<InvoiceResponse> { Data = (await ToDtosAsync([invoice]))[0], Message = "Line removed" });
    }

    [HttpPost("api/v1/invoices/{id}/issue")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Issue(string id)
    {
        var invoice = await LoadAsync(id);
        if (invoice == null) return Fail(404, "Invoice not found");
        if (invoice.Status != "draft") return Fail(400, "This invoice has already been issued");
        if (invoice.Lines.Count == 0 || invoice.Total <= 0) return Fail(400, "There is nothing to bill on this invoice");

        await _billing.IssueAsync(invoice);
        await _audit.AddAsync("invoice.issued", invoice.OwnerId, "invoice", invoice.Id,
            $"Invoice {invoice.Number} issued for {invoice.Period}: {AuditLog.Jod(invoice.Total)}",
            $"إصدار الفاتورة {invoice.Number} عن {invoice.Period}: {AuditLog.Jod(invoice.Total)}");
        await _db.SaveChangesAsync();

        try { await _notifications.NotifyInvoiceIssued(invoice); }
        catch (Exception ex) { _logger.LogWarning(ex, "Invoice notification failed for {InvoiceId}", invoice.Id); }

        return Ok(new ApiResponse<InvoiceResponse> { Data = (await ToDtosAsync([invoice]))[0], Message = $"Invoice {invoice.Number} issued" });
    }

    [HttpPost("api/v1/invoices/{id}/pay")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Pay(string id, [FromBody] PayInvoiceRequest req)
    {
        if (!PayMethods.Contains(req.Method)) return Fail(400, "method must be cliq, bank_transfer or cash");
        var invoice = await LoadAsync(id);
        if (invoice == null) return Fail(404, "Invoice not found");
        if (invoice.Status != "issued") return Fail(400, "Only an issued invoice can be marked paid");

        invoice.Status = "paid";
        invoice.PaidAt = DateTime.UtcNow;
        invoice.PaidMethod = req.Method;
        invoice.PaidReference = string.IsNullOrWhiteSpace(req.Reference) ? null : req.Reference.Trim();
        await _audit.AddAsync("invoice.paid", invoice.OwnerId, "invoice", invoice.Id,
            $"Invoice {invoice.Number} paid ({req.Method}): {AuditLog.Jod(invoice.Total)}",
            $"دفع الفاتورة {invoice.Number} ({req.Method}): {AuditLog.Jod(invoice.Total)}");
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<InvoiceResponse> { Data = (await ToDtosAsync([invoice]))[0], Message = "Payment recorded" });
    }

    [HttpPost("api/v1/invoices/{id}/void")]
    [Authorize(Roles = "super_admin")]
    public async Task<IActionResult> Void(string id, [FromBody] VoidInvoiceRequest? req)
    {
        var invoice = await LoadAsync(id);
        if (invoice == null) return Fail(404, "Invoice not found");
        if (invoice.Status is "paid" or "void") return Fail(400, $"A {invoice.Status} invoice cannot be voided");

        invoice.Status = "void";
        invoice.VoidReason = string.IsNullOrWhiteSpace(req?.Reason) ? null : req!.Reason!.Trim();
        // Voiding a draft the owner never saw is housekeeping; voiding an issued one is news to them.
        if (invoice.Number != null)
            await _audit.AddAsync("invoice.voided", invoice.OwnerId, "invoice", invoice.Id,
                $"Invoice {invoice.Number} voided{(invoice.VoidReason == null ? "" : $": {invoice.VoidReason}")}",
                $"إلغاء الفاتورة {invoice.Number}{(invoice.VoidReason == null ? "" : $": {invoice.VoidReason}")}");
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<InvoiceResponse> { Data = (await ToDtosAsync([invoice]))[0], Message = "Invoice voided" });
    }

    // ------------------------------------------------------------------------------------

    private Task<Invoice?> LoadAsync(string id) =>
        _db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);

    private bool MaySee(Invoice i) =>
        _access.IsAdmin || (_access.IsOwner && i.OwnerId == _access.UserId && i.Status != "draft");

    private async Task<List<InvoiceResponse>> ToDtosAsync(List<Invoice> invoices)
    {
        var ownerIds = invoices.Select(i => i.OwnerId).Distinct().ToList();
        var names = await _db.Companies.AsNoTracking()
            .Where(c => ownerIds.Contains(c.OwnerId))
            .ToDictionaryAsync(c => c.OwnerId, c => (c.Name, c.NameAr));
        var today = PlatformConstants.JordanToday();

        return invoices.Select(i => new InvoiceResponse
        {
            Id = i.Id,
            Number = i.Number,
            OwnerId = i.OwnerId,
            CompanyName = names.TryGetValue(i.OwnerId, out var n) ? n.Name : "",
            CompanyNameAr = names.TryGetValue(i.OwnerId, out var m) ? m.NameAr : null,
            Period = i.Period,
            Status = i.Status,
            Overdue = i.Status == "issued" && i.DueOn < today,
            Total = i.Total,
            IssuedAt = i.IssuedAt,
            DueOn = i.DueOn?.ToString("yyyy-MM-dd"),
            PaidAt = i.PaidAt,
            PaidMethod = i.PaidMethod,
            PaidReference = i.PaidReference,
            VoidReason = i.VoidReason,
            CreatedAt = i.CreatedAt,
            Lines = i.Lines.OrderBy(l => l.Sort).Select(l => new InvoiceLineResponse
            {
                Id = l.Id, Kind = l.Kind, Description = l.Description, DescriptionAr = l.DescriptionAr,
                Quantity = l.Quantity, UnitPrice = l.UnitPrice, Amount = l.Amount,
            }).ToList(),
        }).ToList();
    }
}
