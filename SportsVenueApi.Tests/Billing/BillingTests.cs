using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Billing;
using SportsVenueApi.DTOs.Companies;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Billing;

/// <summary>
/// PlayMaker billing its companies: drafting a month, the trial, the setup fee once, annual
/// plans covering the months after, commission on last month's app bookings, issuing and paying.
/// Each test drafts for its own company only, so the shared database's other companies never
/// change the numbers.
/// </summary>
[Collection("Api")]
public class BillingTests
{
    private readonly DatabaseFixture _fx;
    private readonly HttpClient _admin;

    public BillingTests(DatabaseFixture fx)
    {
        _fx = fx;
        _admin = fx.CreateClientFor(fx.AdminId, "super_admin");
    }

    private static DateTime ThisMonth => new(PlatformConstants.JordanToday().Year, PlatformConstants.JordanToday().Month, 1);
    private static string P(DateTime d) => d.ToString("yyyy-MM");
    private static readonly string NextMonth = P(ThisMonth.AddMonths(1));

    /// <summary>A company with <paramref name="venues"/> active venues, past its trial unless told otherwise.</summary>
    private async Task<User> Company(int venues = 1, bool trialOver = true, object? billing = null)
    {
        var owner = await _fx.CreateOwner();
        for (var i = 0; i < venues; i++) await _fx.CreateBasketballVenue(owner.Id);
        await _admin.GetAsync($"/api/v1/companies/{owner.Id}"); // makes the company, with its trial
        if (trialOver)
            Assert.Equal(HttpStatusCode.OK, (await _admin.PatchAsJsonAsync($"/api/v1/companies/{owner.Id}/billing", new { trialEndsOn = "" })).StatusCode);
        if (billing != null)
            Assert.Equal(HttpStatusCode.OK, (await _admin.PatchAsJsonAsync($"/api/v1/companies/{owner.Id}/billing", billing)).StatusCode);
        return owner;
    }

    private async Task<GenerateInvoicesResponse> Generate(User owner, string period)
    {
        var res = await _admin.PostAsJsonAsync("/api/v1/invoices/generate", new { period, ownerId = owner.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<GenerateInvoicesResponse>>())!.Data!;
    }

    private async Task<InvoiceResponse> Issue(string invoiceId)
    {
        var res = await _admin.PostAsync($"/api/v1/invoices/{invoiceId}/issue", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<InvoiceResponse>>())!.Data!;
    }

    private async Task<CompanyResponse> CompanyOf(User owner) =>
        (await (await _admin.GetAsync($"/api/v1/companies/{owner.Id}")).Content.ReadFromJsonAsync<ApiResponse<CompanyResponse>>())!.Data!;

    [Fact]
    public async Task ANewCompany_StartsOnA30DayTrial_AndIsNotBilledDuringIt()
    {
        var owner = await Company(trialOver: false);

        var billing = (await CompanyOf(owner)).Billing;
        Assert.Equal("trial", billing.Status);
        Assert.Equal(PlatformConstants.JordanToday().AddDays(29).ToString("yyyy-MM-dd"), billing.TrialEndsOn);

        // The trial reaches into this month, so this month is free.
        var result = await Generate(owner, P(ThisMonth));
        Assert.Empty(result.Created);
        Assert.Equal("in_trial", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task AMonth_BillsTheFirstVenue_EachExtraVenue_AndTheSetupFeeOnce()
    {
        var owner = await Company(venues: 3);

        var first = Assert.Single((await Generate(owner, NextMonth)).Created);
        Assert.Equal(new[] { ("subscription", 30.0), ("extra_venues", 30.0), ("setup_fee", 100.0) },
            first.Lines.Select(l => (l.Kind, l.Amount)).ToArray());
        Assert.Equal(160, first.Total, 3);
        Assert.Equal("draft", first.Status);
        Assert.Null(first.Number);

        // A month later there is no second setup fee.
        var second = Assert.Single((await Generate(owner, P(ThisMonth.AddMonths(2)))).Created);
        Assert.DoesNotContain(second.Lines, l => l.Kind == "setup_fee");
        Assert.Equal(60, second.Total, 3);
    }

    [Fact]
    public async Task GeneratingTwice_DoesNotBillTwice_ButAVoidedInvoiceCanBeRedone()
    {
        var owner = await Company();
        var draft = Assert.Single((await Generate(owner, NextMonth)).Created);

        var again = await Generate(owner, NextMonth);
        Assert.Empty(again.Created);
        Assert.Equal("already_invoiced", Assert.Single(again.Skipped).Reason);

        Assert.Equal(HttpStatusCode.OK, (await _admin.PostAsJsonAsync($"/api/v1/invoices/{draft.Id}/void", new { reason = "wrong price" })).StatusCode);
        var redone = Assert.Single((await Generate(owner, NextMonth)).Created);
        // The voided draft's setup fee does not count as charged.
        Assert.Contains(redone.Lines, l => l.Kind == "setup_fee");
    }

    [Fact]
    public async Task ACompanysOwnPrices_AndAWaivedSetupFee_AreUsed()
    {
        var owner = await Company(venues: 2, billing: new { prices = new { firstVenue = 20, extraVenue = 10 }, setupFeeWaived = true });

        var invoice = Assert.Single((await Generate(owner, NextMonth)).Created);

        Assert.Equal(new[] { ("subscription", 20.0), ("extra_venues", 10.0) }, invoice.Lines.Select(l => (l.Kind, l.Amount)).ToArray());
        var billing = (await CompanyOf(owner)).Billing;
        Assert.True(billing.CustomPrices);
        Assert.Equal((20.0, 10.0), (billing.PriceFirstVenue, billing.PriceExtraVenue));
    }

    [Fact]
    public async Task AnAnnualPlan_ChargesTenMonths_CoversTheNextEleven_AndHasNoSetupFee()
    {
        var owner = await Company(billing: new { cycle = "annual" });

        var annual = Assert.Single((await Generate(owner, NextMonth)).Created);
        Assert.Equal(("subscription", 300.0), (Assert.Single(annual.Lines).Kind, annual.Total));

        var covered = await Generate(owner, P(ThisMonth.AddMonths(5)));
        Assert.Equal("nothing_to_bill", Assert.Single(covered.Skipped).Reason);
        Assert.Single((await Generate(owner, P(ThisMonth.AddMonths(13)))).Created); // renewal
    }

    [Fact]
    public async Task Commission_IsLastMonthsAppBookingFees_NotCounterBookingsOrCancelledOnes()
    {
        var owner = await Company();
        var venue = await _fx.CreateBasketballVenue(owner.Id);
        var day = ThisMonth.AddDays(2);
        async Task Booking(bool manual, string status, double fee) => await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = _fx.PlayerId, Sport = "basketball", Date = day, StartTime = "10:00",
            Duration = 60, Amount = 20, TotalAmount = 20, SystemFee = fee, OwnerAmount = 20 - fee,
            IsManual = manual, Status = status, PaymentMethod = "cliq",
        });
        await Booking(false, "completed", 1.0);
        await Booking(false, "confirmed", 1.5);
        await Booking(false, "cancelled", 9.0);
        await Booking(true, "completed", 7.0);

        var invoice = Assert.Single((await Generate(owner, NextMonth)).Created);

        var line = Assert.Single(invoice.Lines, l => l.Kind == "commission");
        Assert.Equal((2.0, 2.5), (line.Quantity, line.Amount));
    }

    [Fact]
    public async Task Issuing_NumbersIt_SetsADueDate_TellsTheOwner_AndOnlyThenTheOwnerSeesIt()
    {
        var owner = await Company();
        var draft = Assert.Single((await Generate(owner, NextMonth)).Created);
        var ownerClient = _fx.CreateClientFor(owner.Id, "venue_owner");

        Assert.Equal(HttpStatusCode.NotFound, (await ownerClient.GetAsync($"/api/v1/invoices/{draft.Id}")).StatusCode);

        var issued = await Issue(draft.Id);

        Assert.Matches(@"^PMJ-\d{4}-\d{4}$", issued.Number);
        Assert.Equal(PlatformConstants.JordanToday().AddDays(14).ToString("yyyy-MM-dd"), issued.DueOn);
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.GetAsync($"/api/v1/invoices/{draft.Id}")).StatusCode);
        var mine = (await (await ownerClient.GetAsync("/api/v1/invoices")).Content.ReadFromJsonAsync<ApiResponse<List<InvoiceResponse>>>())!.Data!;
        Assert.Equal(draft.Id, Assert.Single(mine).Id);

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == owner.Id && n.Type == "invoice_issued" && n.ReferenceId == draft.Id));

        // Another company sees nothing of it.
        var other = await _fx.CreateOwner();
        Assert.Equal(HttpStatusCode.NotFound,
            (await _fx.CreateClientFor(other.Id, "venue_owner").GetAsync($"/api/v1/invoices/{draft.Id}")).StatusCode);
    }

    [Fact]
    public async Task ADraftCanBeAdjusted_AnIssuedOneCannot()
    {
        var owner = await Company(billing: new { setupFeeWaived = true });
        var draft = Assert.Single((await Generate(owner, NextMonth)).Created);

        var res = await _admin.PostAsJsonAsync($"/api/v1/invoices/{draft.Id}/lines", new { description = "Launch discount", amount = -10 });
        Assert.Equal(20, (await res.Content.ReadFromJsonAsync<ApiResponse<InvoiceResponse>>())!.Data!.Total, 3);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync($"/api/v1/invoices/{draft.Id}/lines", new { description = "Too much", amount = -50 })).StatusCode);

        await Issue(draft.Id);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync($"/api/v1/invoices/{draft.Id}/lines", new { description = "Late", amount = 5 })).StatusCode);
    }

    [Fact]
    public async Task AnUnpaidInvoicePastItsDueDate_IsOverdue_UntilPaid()
    {
        var owner = await Company(billing: new { setupFeeWaived = true });
        var invoice = await Issue(Assert.Single((await Generate(owner, NextMonth)).Created).Id);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.Invoices.FirstAsync(i => i.Id == invoice.Id);
            row.DueOn = PlatformConstants.JordanToday().AddDays(-1);
            await db.SaveChangesAsync();
        }

        var billing = (await CompanyOf(owner)).Billing;
        Assert.Equal((1, 30.0), (billing.OverdueCount, billing.OverdueAmount));
        // The owner's own view carries it too — that is what the banner reads.
        var mine = (await (await _fx.CreateClientFor(owner.Id, "venue_owner").GetAsync("/api/v1/companies/me"))
            .Content.ReadFromJsonAsync<ApiResponse<CompanyResponse>>())!.Data!;
        Assert.Equal(1, mine.Billing.OverdueCount);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync($"/api/v1/invoices/{invoice.Id}/pay", new { method = "gold" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync($"/api/v1/invoices/{invoice.Id}/pay", new { method = "cliq", reference = "TX-1" })).StatusCode);
        Assert.Equal(0, (await CompanyOf(owner)).Billing.OverdueCount);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync($"/api/v1/invoices/{invoice.Id}/void", new { })).StatusCode); // paid stays paid
    }

    [Fact]
    public async Task OnlyTheAdminRunsBilling()
    {
        var owner = await Company();
        var ownerClient = _fx.CreateClientFor(owner.Id, "venue_owner");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await ownerClient.PostAsJsonAsync("/api/v1/invoices/generate", new { period = NextMonth })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await ownerClient.PatchAsJsonAsync($"/api/v1/companies/{owner.Id}/billing", new { setupFeeWaived = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _fx.CreateClientFor(_fx.StaffAWriteId, "venue_staff", _fx.OwnerAId, "write").GetAsync("/api/v1/invoices")).StatusCode);
    }

    [Fact]
    public async Task BillingDefaults_LiveInSettings()
    {
        var res = await _admin.GetAsync("/api/v1/settings");
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"priceFirstVenue\":30", body);
        Assert.Contains("\"priceExtraVenue\":15", body);

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PatchAsJsonAsync("/api/v1/settings", new
        {
            billing = new { priceFirstVenue = -1, priceExtraVenue = 15, setupFee = 100, trialDays = 30, paymentTermsDays = 14 },
        })).StatusCode);
    }
}
