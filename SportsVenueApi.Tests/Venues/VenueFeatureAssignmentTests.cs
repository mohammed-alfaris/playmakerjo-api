using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.VenueFeatures;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Venues;

/// <summary>
/// An owner describing their venue: ticking catalog features, typing their own.
///
/// The interesting rules are the ones that protect the data rather than the form. A typed label
/// that is really a catalog feature becomes that feature, or players filtering for it never find
/// the venue. A feature an admin retires stays on venues that already chose it — editing an old
/// venue must not start failing — but cannot be newly added.
/// </summary>
[Collection("Api")]
public class VenueFeatureAssignmentTests
{
    private readonly DatabaseFixture _fx;

    public VenueFeatureAssignmentTests(DatabaseFixture fx) => _fx = fx;

    private HttpClient Owner => _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
    private HttpClient Admin => _fx.CreateClientFor(_fx.AdminId, "super_admin");

    private static object NewVenue(string[]? featureIds = null, string[]? customFeatures = null) => new
    {
        name = "Feature Venue " + Guid.NewGuid().ToString("N")[..6],
        city = "Amman",
        address = "Features Street 1",
        pricePerHour = 20,
        sports = new[] { "basketball" },
        status = "active",
        featureIds,
        customFeatures,
    };

    private async Task<VenueResponse> CreateVenue(string[]? featureIds = null, string[]? customFeatures = null)
    {
        var res = await Owner.PostAsJsonAsync("/api/v1/venues", NewVenue(featureIds, customFeatures));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<VenueResponse>>())!.Data!;
    }

    private async Task<VenueResponse> PublicDetail(string venueId)
    {
        var res = await _fx.Factory.CreateClient().GetAsync($"/api/v1/venues/public/{venueId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<VenueResponse>>())!.Data!;
    }

    private async Task<string> Message(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<ApiResponse<object>>())?.Message ?? "";

    private async Task<VenueFeatureResponse> RetiredThrowaway()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var created = await Admin.PostAsJsonAsync("/api/v1/venue-features",
            new { name = $"Sauna {suffix}", nameAr = $"ساونا {suffix}", icon = "indoor" });
        var feature = (await created.Content.ReadFromJsonAsync<ApiResponse<VenueFeatureResponse>>())!.Data!;
        var retire = await Admin.PatchAsJsonAsync($"/api/v1/venue-features/{feature.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        return feature;
    }

    // --------------------------------------------------------------- the happy path

    [Fact]
    public async Task CatalogAndTypedFeatures_ReachThePublicVenuePage()
    {
        // Sent out of catalog order on purpose.
        var venue = await CreateVenue(["vf-showers", "vf-parking"], ["Shaded benches"]);

        var detail = await PublicDetail(venue.Id);

        Assert.Equal(["vf-parking", "vf-showers"], detail.Features.Select(f => f.Id));
        var parking = detail.Features[0];
        Assert.Equal("Parking", parking.Name);
        Assert.Equal("موقف سيارات", parking.NameAr);
        Assert.Equal("parking", parking.Icon);
        Assert.Equal(["Shaded benches"], detail.CustomFeatures);
    }

    [Fact]
    public async Task AVenueWithNoFeatures_ReturnsEmptyLists_NotNull()
    {
        var venue = await CreateVenue();

        var detail = await PublicDetail(venue.Id);

        Assert.Empty(detail.Features);
        Assert.Empty(detail.CustomFeatures);
    }

    // --------------------------------------------------------------- what gets refused

    [Fact]
    public async Task AnUnknownFeatureId_IsRefused()
    {
        var res = await Owner.PostAsJsonAsync("/api/v1/venues", NewVenue(["vf-helipad"]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("Unknown feature", await Message(res));
    }

    [Fact]
    public async Task MoreThanFifteenTypedFeatures_IsRefused()
    {
        var labels = Enumerable.Range(1, 16).Select(i => $"Extra {i}").ToArray();

        var res = await Owner.PostAsJsonAsync("/api/v1/venues", NewVenue(customFeatures: labels));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ATypedFeatureOverFortyCharacters_IsRefused()
    {
        var res = await Owner.PostAsJsonAsync("/api/v1/venues", NewVenue(customFeatures: [new string('x', 41)]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Staff_CannotChangeAVenuesFeatures()
    {
        var venue = await CreateVenue(["vf-parking"]);
        var clerk = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        var res = await clerk.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new { featureIds = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(["vf-parking"], (await PublicDetail(venue.Id)).Features.Select(f => f.Id));
    }

    // --------------------------------------------------------------- typed labels

    [Fact]
    public async Task ATypedLabelThatIsReallyACatalogFeature_BecomesThatFeature()
    {
        // "parking" typed by hand, and the prayer room typed in Arabic.
        var venue = await CreateVenue(customFeatures: ["  parking ", "مصلى"]);

        var detail = await PublicDetail(venue.Id);

        Assert.Equal(["vf-parking", "vf-prayer-room"], detail.Features.Select(f => f.Id));
        Assert.Empty(detail.CustomFeatures);
    }

    [Fact]
    public async Task TypedLabels_AreTrimmedCollapsedAndDeduplicated()
    {
        var venue = await CreateVenue(customFeatures: ["  Kids   corner ", "kids corner", "", "   ", "Shade"]);

        var detail = await PublicDetail(venue.Id);

        Assert.Equal(["Kids corner", "Shade"], detail.CustomFeatures);
    }

    // --------------------------------------------------------------- editing

    [Fact]
    public async Task SendingOnlyTypedFeatures_KeepsTheCatalogOnes()
    {
        var venue = await CreateVenue(["vf-parking"]);

        var res = await Owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new { customFeatures = new[] { "Shade" } });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var detail = await PublicDetail(venue.Id);
        Assert.Equal(["vf-parking"], detail.Features.Select(f => f.Id));
        Assert.Equal(["Shade"], detail.CustomFeatures);
    }

    [Fact]
    public async Task EmptyLists_ClearEverything()
    {
        var venue = await CreateVenue(["vf-parking"], ["Shade"]);

        var res = await Owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}",
            new { featureIds = Array.Empty<string>(), customFeatures = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var detail = await PublicDetail(venue.Id);
        Assert.Empty(detail.Features);
        Assert.Empty(detail.CustomFeatures);
    }

    [Fact]
    public async Task EditingOtherFields_LeavesFeaturesAlone()
    {
        var venue = await CreateVenue(["vf-parking"], ["Shade"]);

        var res = await Owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new { description = "Now with a new description" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var detail = await PublicDetail(venue.Id);
        Assert.Equal(["vf-parking"], detail.Features.Select(f => f.Id));
        Assert.Equal(["Shade"], detail.CustomFeatures);
    }

    // --------------------------------------------------------------- retired features

    [Fact]
    public async Task ARetiredFeature_CannotBeNewlyAdded()
    {
        var retired = await RetiredThrowaway();

        var res = await Owner.PostAsJsonAsync("/api/v1/venues", NewVenue([retired.Id]));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("no longer offered", await Message(res));
    }

    [Fact]
    public async Task ARetiredFeature_StaysOnAVenueThatAlreadyChoseIt_ThroughAnEdit()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var created = await Admin.PostAsJsonAsync("/api/v1/venue-features",
            new { name = $"Hammam {suffix}", nameAr = $"حمام {suffix}", icon = "shower" });
        var feature = (await created.Content.ReadFromJsonAsync<ApiResponse<VenueFeatureResponse>>())!.Data!;
        var venue = await CreateVenue([feature.Id, "vf-parking"]);

        await Admin.PatchAsJsonAsync($"/api/v1/venue-features/{feature.Id}", new { isActive = false });

        // The owner re-saves the whole form, retired feature still ticked.
        var res = await Owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}",
            new { featureIds = new[] { feature.Id, "vf-parking" }, customFeatures = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains((await PublicDetail(venue.Id)).Features, f => f.Id == feature.Id);
    }

    [Fact]
    public async Task RenamingACatalogFeature_ShowsTheNewNameOnVenuesThatHaveIt()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var created = await Admin.PostAsJsonAsync("/api/v1/venue-features",
            new { name = $"Juice bar {suffix}", nameAr = $"عصائر {suffix}", icon = "cafe" });
        var feature = (await created.Content.ReadFromJsonAsync<ApiResponse<VenueFeatureResponse>>())!.Data!;
        var venue = await CreateVenue([feature.Id]);

        await Admin.PatchAsJsonAsync($"/api/v1/venue-features/{feature.Id}", new { name = $"Smoothie bar {suffix}" });

        Assert.Equal($"Smoothie bar {suffix}", Assert.Single((await PublicDetail(venue.Id)).Features).Name);
    }
}
