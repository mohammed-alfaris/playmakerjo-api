using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Services;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// The product is sold as a subscription to venue owners who are COMPETITORS sharing one
/// database and one server. Everything here therefore asserts in both directions: the
/// wrong caller is refused, AND the right caller still gets their own data (a filter that
/// returns nothing is not a fix, it is a different bug).
///
/// Every endpoint below previously used an allow-by-default deny-list of the shape
/// <c>if (UserRole == "venue_owner" &amp;&amp; ...) return Forbid();</c>, which named the roles to
/// block and let every other role — player, venue_staff, anything unrecognised — through
/// to the unfiltered platform-wide branch.
/// </summary>
[Collection("Api")]
public class TenantIsolationTests
{
    private readonly DatabaseFixture _fx;

    public TenantIsolationTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(7).ToString("yyyy-MM-dd");

    private static object BookingBody(string venueId, string startTime = "10:00") => new
    {
        venueId,
        sport = "basketball",
        date = FutureDate,
        startTime,
        duration = 60,
        paymentMethod = "cliq",
    };

    /// <summary>Creates a confirmed-path booking on the given venue, made by the shared test player.</summary>
    private async Task<string> SeedBookingAsync(string venueId, string startTime = "10:00")
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings", BookingBody(venueId, startTime));
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        return body!.Data!.Id;
    }

    // ---------------------------------------------------------------- reports

    [Fact]
    public async Task Export_AsPlayer_IsRefused()
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.GetAsync("/api/v1/reports/export?format=csv");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Export_AsOwner_ContainsOwnVenueAndNotCompetitors()
    {
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.Name = "Isolation Export A");
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId, v => v.Name = "Isolation Export B");
        await SeedBookingAsync(venueA.Id, "11:00");
        await SeedBookingAsync(venueB.Id, "12:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/reports/export?format=csv");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var csv = await res.Content.ReadAsStringAsync();
        Assert.Contains("Isolation Export A", csv);      // positive: own data is present
        Assert.DoesNotContain("Isolation Export B", csv); // negative: the competitor is not
    }

    [Fact]
    public async Task Export_AsOwner_CannotTargetACompetitorVenueDirectly()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId, v => v.Name = "Isolation Target B");
        await SeedBookingAsync(venueB.Id, "13:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/reports/export?format=csv&venue_id={venueB.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var csv = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Isolation Target B", csv);
    }

    [Fact]
    public async Task Export_QuotesFieldsAndNeutralisesFormulas()
    {
        // A venue name containing a comma used to corrupt the row; one starting with '='
        // was evaluated as a formula when the CSV was opened in a spreadsheet.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.Name = "=CMD,Injection Test");
        await SeedBookingAsync(venue.Id, "14:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var csv = await (await client.GetAsync("/api/v1/reports/export?format=csv")).Content.ReadAsStringAsync();

        Assert.Contains("\"'=CMD,Injection Test\"", csv);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("revenue-chart")]
    [InlineData("top-venues")]
    [InlineData("sports-breakdown")]
    public async Task ReportEndpoints_AsPlayer_AreRefused(string endpoint)
    {
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.GetAsync($"/api/v1/reports/{endpoint}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("revenue-chart")]
    [InlineData("top-venues")]
    [InlineData("sports-breakdown")]
    public async Task ReportEndpoints_AsUnknownRole_AreRefused(string endpoint)
    {
        // venue_staff is a real role this product creates, and it matched none of the old
        // deny-lists — so it received platform-wide aggregates.
        var client = _fx.CreateClientFor("test-staff-unlinked", "venue_staff");
        var res = await client.GetAsync($"/api/v1/reports/{endpoint}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task TopVenues_AsOwner_ExcludesCompetitorVenues()
    {
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.Name = "Isolation Top A");
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId, v => v.Name = "Isolation Top B");
        await SeedBookingAsync(venueA.Id, "15:00");
        await SeedBookingAsync(venueB.Id, "16:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/reports/top-venues");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var payload = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Isolation Top B", payload);
    }

    [Fact]
    public async Task Summary_AsOwner_IgnoresAClientSuppliedOwnerId()
    {
        // Scope must come from the token. Passing a competitor's id must not widen it.
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var mine = await client.GetAsync("/api/v1/reports/summary");
        var spoofed = await client.GetAsync($"/api/v1/reports/summary?owner_id={_fx.OwnerBId}");

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(HttpStatusCode.OK, spoofed.StatusCode);
        Assert.Equal(await mine.Content.ReadAsStringAsync(), await spoofed.Content.ReadAsStringAsync());
    }

    // --------------------------------------------------------------- bookings

    [Fact]
    public async Task BookingsList_AsPlayer_IsRefused()
    {
        // Players have /bookings/my; the back-office list used to hand them every
        // booking on the platform, including other customers' names.
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task BookingsList_AsOwner_ShowsOwnAndHidesCompetitors()
    {
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var mine = await SeedBookingAsync(venueA.Id, "17:00");
        var theirs = await SeedBookingAsync(venueB.Id, "18:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/bookings?limit=200");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var payload = await res.Content.ReadAsStringAsync();
        Assert.Contains(mine, payload);
        Assert.DoesNotContain(theirs, payload);
    }

    [Fact]
    public async Task BookingsList_AsOwner_IgnoresAClientSuppliedOwnerId()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var theirs = await SeedBookingAsync(venueB.Id, "19:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/bookings?limit=200&owner_id={_fx.OwnerBId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain(theirs, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetBooking_AsCompetitorOwner_IsRefused()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var id = await SeedBookingAsync(venueB.Id, "20:00");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync($"/api/v1/bookings/{id}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("no-show")]
    [InlineData("review-proof")]
    public async Task BookingMutations_AsUnknownRole_AreRefused(string action)
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var id = await SeedBookingAsync(venue.Id, "21:00");

        var client = _fx.CreateClientFor("test-staff-unlinked", "venue_staff");
        var res = await client.PatchAsJsonAsync($"/api/v1/bookings/{id}/{action}", new { approved = true });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ----------------------------------------------------------------- venues

    [Fact]
    public async Task VenuesList_AsOwner_ShowsOwnAndHidesCompetitors()
    {
        var mine = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.Name = "Isolation Venue Mine");
        var theirs = await _fx.CreateBasketballVenue(_fx.OwnerBId, v => v.Name = "Isolation Venue Theirs");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.GetAsync("/api/v1/venues?limit=200");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var payload = await res.Content.ReadAsStringAsync();
        Assert.Contains(mine.Id, payload);
        Assert.DoesNotContain(theirs.Id, payload);
    }

    [Fact]
    public async Task VenuesList_AsPlayer_IsRefused()
    {
        // Public discovery lives on /venues/public and /venues/search. This route is the
        // back office and used to hand out every venue's pricing and CliQ alias.
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.GetAsync("/api/v1/venues?limit=200");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task VenuesList_AsUnlinkedStaff_IsRefused()
    {
        var client = _fx.CreateClientFor(_fx.StaffUnlinkedId, "venue_staff");
        var res = await client.GetAsync("/api/v1/venues?limit=200");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ------------------------------------------------------- payment bypass

    [Fact]
    public async Task Confirm_AsPlayer_OnOwnBooking_IsRefusedAndLeavesStatusUnchanged()
    {
        // The old guard rejected a player acting on someone ELSE's booking, which meant
        // acting on your own was permitted: create pending_payment, call confirm, and walk
        // onto the pitch with depositPaid=true having transferred nothing.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var id = await SeedBookingAsync(venue.Id, "09:00");

        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PatchAsync($"/api/v1/bookings/{id}/confirm", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var row = await _fx.LoadBooking(id);
        Assert.Equal("pending_payment", row!.Status);
        Assert.False(row.DepositPaid);
        Assert.Equal(0, row.AmountPaid, 3);
    }

    [Fact]
    public async Task Confirm_AsOwningVenue_StillWorks()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var id = await SeedBookingAsync(venue.Id, "08:30");

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{id}/confirm", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = await _fx.LoadBooking(id);
        Assert.Equal("confirmed", row!.Status);
    }

    // ------------------------------------------------------------ token type

    [Fact]
    public async Task RefreshToken_UsedAsBearer_IsRejected()
    {
        // Access and refresh tokens share a key, issuer and claims and differ only by
        // "type". Without validating it, a refresh token authenticated everywhere and
        // silently extended the 15-minute window to 7 days.
        var jwt = _fx.Factory.Services.GetRequiredService<JwtService>();
        var client = _fx.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt.CreateRefreshToken(_fx.AdminId, "super_admin"));

        var res = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task AccessToken_StillWorks()
    {
        var client = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var res = await client.GetAsync("/api/v1/bookings");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
