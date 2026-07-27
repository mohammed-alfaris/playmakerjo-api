using System.Net;
using System.Net.Http.Json;
using System.Text;
using SportsVenueApi.Constants;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Customers;

/// <summary>
/// The owner can take his customer list and walk out, at any time, without asking.
///
/// This is a commercial promise before it is a technical one: it is made explicitly in the
/// sales conversation, in a market where owners have been burned by platforms that held
/// their customer list hostage. A promise nobody tested is a promise waiting to be broken,
/// so these tests exist to keep it honest — especially the one asserting it is NOT gated.
/// </summary>
[Collection("Api")]
public class CustomerExportTests
{
    private readonly DatabaseFixture _fx;

    public CustomerExportTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(23).ToString("yyyy-MM-dd");

    private async Task<string> BookFor(string venueId, string time, string phone, string name)
    {
        var res = await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .PostAsJsonAsync("/api/v1/bookings", new
            {
                venueId, sport = "basketball", date = FutureDate, startTime = time,
                duration = 60, paymentMethod = "cliq", isManual = true,
                customerPhone = phone, customerName = name,
            });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return phone;
    }

    private static async Task<string> BodyOf(HttpResponseMessage res) =>
        Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());

    [Fact]
    public async Task AnOwnerCanExportHisOwnCustomers()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        await BookFor(venue.Id, "09:00", "0791234801", "خالد النتور");

        var res = await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner").GetAsync("/api/v1/customers/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);

        var csv = await BodyOf(res);
        Assert.Contains("خالد النتور", csv);
        Assert.Contains("+962791234801", csv);
        Assert.Contains("Name,Phone,Bookings", csv);
    }

    [Fact]
    public async Task TheExportOpensCorrectlyInExcel()
    {
        // Without a BOM, Excel on Windows reads UTF-8 as cp1256 and every Arabic name
        // becomes mojibake. The endpoint would return 200 and the file would be useless —
        // the worst kind of failure, because it looks like it worked.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        await BookFor(venue.Id, "10:00", "0791234802", "سامي عبد الله");

        var bytes = await (await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .GetAsync("/api/v1/customers/export")).Content.ReadAsByteArrayAsync();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
    }

    [Fact]
    public async Task ANameStartingWithAnEqualsSignIsNotExecutedByExcel()
    {
        // A customer types =cmd|'/c calc'!A1 as their name. The owner exports his own
        // customers, opens the file, and runs it. The name is captured from an untrusted
        // field, so it must be defused on the way out.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        await BookFor(venue.Id, "11:00", "0791234803", "=1+1");

        var csv = await BodyOf(await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .GetAsync("/api/v1/customers/export"));

        Assert.Contains("\"'=1+1\"", csv);
        Assert.DoesNotContain("\"=1+1\"", csv);
    }

    [Fact]
    public async Task ArchivedCustomersAreStillIncluded()
    {
        // Leaving someone out because a flag was flipped in OUR product would hand the
        // owner a quietly incomplete file, which is worse than offering no export at all.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        await BookFor(venue.Id, "12:00", "0791234804", "ليث الحديد");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var list = await client.GetFromJsonAsync<System.Text.Json.JsonDocument>(
            "/api/v1/customers?limit=100&search=0791234804");
        var id = list!.RootElement.GetProperty("data")[0].GetProperty("id").GetString();

        var archive = await client.PatchAsync($"/api/v1/customers/{id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);

        Assert.Contains("ليث الحديد", await BodyOf(await client.GetAsync("/api/v1/customers/export")));
    }

    [Fact]
    public async Task OneOwnersExportNeverContainsAnothersCustomers()
    {
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);

        await BookFor(venueA.Id, "13:00", "0791234805", "زبون مالك أ");
        var resB = await _fx.CreateClientFor(_fx.OwnerBId, "venue_owner")
            .PostAsJsonAsync("/api/v1/bookings", new
            {
                venueId = venueB.Id, sport = "basketball", date = FutureDate, startTime = "13:00",
                duration = 60, paymentMethod = "cliq", isManual = true,
                customerPhone = "0791234806", customerName = "زبون مالك ب",
            });
        Assert.Equal(HttpStatusCode.OK, resB.StatusCode);

        var csvA = await BodyOf(await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .GetAsync("/api/v1/customers/export"));

        Assert.Contains("زبون مالك أ", csvA);
        Assert.DoesNotContain("زبون مالك ب", csvA);
    }

    [Fact]
    public async Task APlayerCannotExportAnyonesCustomerBook()
    {
        var player = await _fx.CreatePlayer();
        var res = await _fx.CreateClientFor(player.Id, "player").GetAsync("/api/v1/customers/export");

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
