using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.Services;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Demo;

/// <summary>
/// The billing demo: what it seeds is what the billing screens need to show (an overdue
/// company, a company with months to invoice), a second run changes nothing, and removing it
/// leaves no row behind — including the invoices generated while trying it out.
/// </summary>
[Collection("Api")]
public class DemoBillingSeedTests
{
    private readonly DatabaseFixture _fx;

    public DemoBillingSeedTests(DatabaseFixture fx) => _fx = fx;

    [Fact]
    public async Task SeedsAnOverdueAndAnInvoiceableCompany_AndRemovesWithoutTrace()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<BillingService>();
        await DemoBillingSeed.RemoveAsync(db);   // a leftover from an earlier run, if any

        await DemoBillingSeed.RunAsync(db, 5);
        var again = await DemoBillingSeed.RunAsync(db, 5);
        Assert.Contains("nothing to do", again);

        var (overdue, amount) = await billing.OverdueAsync("demo_bill_own1");
        Assert.Equal(2, overdue);
        Assert.True(amount > 0);
        Assert.All(await db.Invoices.Where(i => i.OwnerId == "demo_bill_own1").ToListAsync(),
            i => Assert.StartsWith("DEMO-", i.Number));

        // The active company bills a small venue (50), a large one (75) and commission on last month.
        var today = PlatformConstants.JordanToday();
        var lastMonth = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
        var result = await billing.GenerateAsync(lastMonth, null, "demo_bill_own2");
        var invoice = Assert.Single(result.Created);
        var lines = await db.InvoiceLines.Where(l => l.InvoiceId == invoice.Id).ToListAsync();
        Assert.Contains(lines, l => l.Kind == "subscription" && l.Amount == 50);
        Assert.Contains(lines, l => l.Kind == "subscription" && l.Amount == 75);
        Assert.Contains(lines, l => l.Kind == "commission" && l.Amount > 0);

        await DemoBillingSeed.RemoveAsync(db);
        Assert.False(await db.Users.AnyAsync(u => u.Id.StartsWith("demo_bill_")));
        Assert.False(await db.Venues.AnyAsync(v => v.Id.StartsWith("demo_bill_")));
        Assert.False(await db.Bookings.AnyAsync(b => b.Id.StartsWith("demo_bill_")));
        Assert.False(await db.Invoices.AnyAsync(i => i.OwnerId.StartsWith("demo_bill_")));
        Assert.False(await db.Companies.AnyAsync(c => c.OwnerId.StartsWith("demo_bill_")));
    }
}
