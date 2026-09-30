using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services;

/// <summary>
/// PlayMaker's own billing: drafting each company's invoice for a month, numbering it when it
/// is issued, and summarising where a company stands (trial, overdue, suspended).
///
/// A month's invoice carries the subscription for THAT month (in advance) and the commission
/// on the PREVIOUS month's app bookings (in arrears) — the fee rate each booking was taken at,
/// already stored on it as SystemFee, so changing the rate in Settings never re-prices history.
/// </summary>
public sealed class BillingService
{
    public const string Monthly = "monthly";
    public const string Annual = "annual";

    /// <summary>Booking statuses a commission is owed on: the game was sold through the app.</summary>
    private static readonly string[] CommissionStatuses = ["confirmed", "completed", "no_show"];

    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly CompanyService _companies;

    public BillingService(AppDbContext db, SettingsService settings, CompanyService companies)
    {
        _db = db;
        _settings = settings;
        _companies = companies;
    }

    public static bool TryParsePeriod(string? period, out DateTime monthStart) =>
        DateTime.TryParseExact(period, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out monthStart);

    public static string PeriodOf(DateTime d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static double R(double x) => Math.Round(x, 3);

    // ── Where a company stands ──────────────────────────────────────────────────────────

    /// <summary>"suspended", else "trial" while the trial lasts, else "active".</summary>
    public static string StatusOf(Company c, DateTime today) =>
        c.SuspendedAt != null ? "suspended"
        : c.TrialEndsOn is { } end && end.Date >= today ? "trial"
        : "active";

    public async Task<(double First, double Extra)> PricesForAsync(Company c)
    {
        var s = await _settings.GetAsync();
        return (c.PriceFirstVenue ?? s.PriceFirstVenue, c.PriceExtraVenue ?? s.PriceExtraVenue);
    }

    /// <summary>Issued invoices past their due date.</summary>
    public async Task<(int Count, double Amount)> OverdueAsync(string ownerId)
    {
        var today = PlatformConstants.JordanToday();
        var rows = await _db.Invoices.AsNoTracking()
            .Where(i => i.OwnerId == ownerId && i.Status == "issued" && i.DueOn < today)
            .Select(i => i.Total)
            .ToListAsync();
        return (rows.Count, R(rows.Sum()));
    }

    public Task<bool> IsSuspendedAsync(string ownerId) =>
        _db.Companies.AnyAsync(c => c.OwnerId == ownerId && c.SuspendedAt != null);

    // ── Drafting a month ────────────────────────────────────────────────────────────────

    public sealed record Skipped(string OwnerId, string CompanyName, string Reason);
    public sealed record GenerateResult(List<Invoice> Created, List<Skipped> Skipped);

    /// <summary>
    /// Drafts the month's invoice for every company that has something to pay and no invoice
    /// for the month yet (a voided one does not count). Safe to press twice: the second press
    /// only fills in companies the first one skipped or that were added since.
    /// </summary>
    public async Task<GenerateResult> GenerateAsync(DateTime monthStart, string? actorUserId, string? onlyOwnerId = null)
    {
        monthStart = new DateTime(monthStart.Year, monthStart.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var prevStart = monthStart.AddMonths(-1);
        var period = PeriodOf(monthStart);
        var settings = await _settings.GetAsync();

        var owners = await _db.Users.AsNoTracking()
            .Where(u => u.Role == "venue_owner" && (onlyOwnerId == null || u.Id == onlyOwnerId))
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Status })
            .ToListAsync();

        var created = new List<Invoice>();
        var skipped = new List<Skipped>();

        foreach (var owner in owners)
        {
            var company = await _companies.EnsureAsync(owner.Id);

            if (owner.Status != "active") { skipped.Add(new(owner.Id, company.Name, "owner_inactive")); continue; }
            if (company.SuspendedAt != null) { skipped.Add(new(owner.Id, company.Name, "suspended")); continue; }
            if (await _db.Invoices.AnyAsync(i => i.OwnerId == owner.Id && i.Period == period && i.Status != "void"))
            {
                skipped.Add(new(owner.Id, company.Name, "already_invoiced"));
                continue;
            }

            var lines = new List<InvoiceLine>();
            var inTrial = company.TrialEndsOn is { } trialEnd && trialEnd.Date >= monthStart;

            // Subscription, unless the trial reaches into this month or an annual payment covers it.
            if (!inTrial && !await CoveredAsync(owner.Id, monthStart))
            {
                var venues = await _db.Venues.CountAsync(v => v.OwnerId == owner.Id && v.Status == "active");
                if (venues > 0)
                {
                    var (first, extra) = (company.PriceFirstVenue ?? settings.PriceFirstVenue,
                                          company.PriceExtraVenue ?? settings.PriceExtraVenue);
                    var annual = company.BillingCycle == Annual;
                    var months = annual ? 10 : 1;          // twelve months for the price of ten
                    var coversTo = annual ? monthStart.AddMonths(12) : monthEnd;
                    var label = annual ? $"Annual subscription ({period} for 12 months, 10 paid)" : $"Monthly subscription ({period})";
                    var labelAr = annual ? $"اشتراك سنوي ({period} لمدة ١٢ شهر، مدفوع ١٠)" : $"الاشتراك الشهري ({period})";

                    lines.Add(new InvoiceLine
                    {
                        Kind = "subscription", Description = $"{label}: first venue", DescriptionAr = $"{labelAr}: الملعب الأول",
                        Quantity = months, UnitPrice = first, Amount = R(months * first),
                        CoversFrom = monthStart, CoversTo = coversTo,
                    });
                    if (venues > 1)
                        lines.Add(new InvoiceLine
                        {
                            Kind = "extra_venues",
                            Description = $"{label}: {venues - 1} additional venue(s)",
                            DescriptionAr = $"{labelAr}: {venues - 1} ملعب إضافي",
                            Quantity = months * (venues - 1), UnitPrice = extra, Amount = R(months * (venues - 1) * extra),
                            CoversFrom = monthStart, CoversTo = coversTo,
                        });

                    // Once per company, with its first paid month — never on an annual plan.
                    if (!annual && !company.SetupFeeWaived && settings.SetupFee > 0
                        && !await _db.InvoiceLines.AnyAsync(l => l.Kind == "setup_fee"
                            && l.Invoice.OwnerId == owner.Id && l.Invoice.Status != "void"))
                        lines.Add(new InvoiceLine
                        {
                            Kind = "setup_fee", Description = "Setup: data entry and training", DescriptionAr = "رسوم التأسيس: إدخال البيانات والتدريب",
                            Quantity = 1, UnitPrice = settings.SetupFee, Amount = R(settings.SetupFee),
                        });
                }
            }

            // Commission on last month's app bookings, whatever the subscription is doing: it
            // pays for customers the platform brought, not for the software.
            var fees = await _db.Bookings.AsNoTracking()
                .Where(b => b.Venue.OwnerId == owner.Id && !b.IsManual
                    && CommissionStatuses.Contains(b.Status)
                    && b.Date >= prevStart && b.Date < monthStart)
                .Select(b => b.SystemFee)
                .ToListAsync();
            var commission = R(fees.Sum());
            if (commission > 0)
                lines.Add(new InvoiceLine
                {
                    Kind = "commission",
                    Description = $"Commission on {fees.Count} app booking(s) in {PeriodOf(prevStart)}",
                    DescriptionAr = $"عمولة {fees.Count} حجز من التطبيق في {PeriodOf(prevStart)}",
                    Quantity = fees.Count, UnitPrice = R(commission / fees.Count), Amount = commission,
                });

            if (lines.Count == 0)
            {
                skipped.Add(new(owner.Id, company.Name, inTrial ? "in_trial" : "nothing_to_bill"));
                continue;
            }

            for (var i = 0; i < lines.Count; i++) lines[i].Sort = i;
            var invoice = new Invoice
            {
                OwnerId = owner.Id,
                Period = period,
                Lines = lines,
                Total = R(lines.Sum(l => l.Amount)),
                CreatedByUserId = actorUserId,
            };
            _db.Invoices.Add(invoice);
            await _db.SaveChangesAsync();
            created.Add(invoice);
        }

        return new GenerateResult(created, skipped);
    }

    /// <summary>Is this month already paid for by a subscription line on a live invoice (an annual plan)?</summary>
    private Task<bool> CoveredAsync(string ownerId, DateTime monthStart) =>
        _db.InvoiceLines.AnyAsync(l => l.Kind == "subscription"
            && l.Invoice.OwnerId == ownerId && l.Invoice.Status != "void"
            && l.CoversFrom <= monthStart && l.CoversTo > monthStart);

    public static void Recalculate(Invoice invoice) =>
        invoice.Total = R(invoice.Lines.Sum(l => l.Amount));

    /// <summary>
    /// Issues a draft: gives it the next number of the year and a due date. Numbers are only
    /// given here, so the sequence has no holes from voided drafts.
    /// </summary>
    public async Task IssueAsync(Invoice invoice)
    {
        var settings = await _settings.GetAsync();
        var today = PlatformConstants.JordanToday();
        var prefix = $"PMJ-{today:yyyy}-";

        for (var attempt = 0; ; attempt++)
        {
            var last = await _db.Invoices.AsNoTracking()
                .Where(i => i.Number != null && i.Number.StartsWith(prefix))
                .OrderByDescending(i => i.Number)
                .Select(i => i.Number)
                .FirstOrDefaultAsync();
            var next = last == null ? 1 : int.Parse(last[prefix.Length..], CultureInfo.InvariantCulture) + 1;

            invoice.Number = $"{prefix}{next:0000}";
            invoice.Status = "issued";
            invoice.IssuedAt = DateTime.UtcNow;
            invoice.DueOn = today.AddDays(settings.PaymentTermsDays);
            try
            {
                await _db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                // Another issue took the number first; take the next one.
            }
        }
    }
}
