using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.VenueFeatures;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Venues;

/// <summary>
/// The catalog of venue features: super_admin curates it, everyone may read it.
///
/// The fixture database is shared by the whole suite, and the assignment and search tests rely
/// on the migration's starter rows. So nothing here renames, retires or deletes a starter
/// feature — every test that changes the catalog makes its own throwaway feature first.
/// </summary>
[Collection("Api")]
public class VenueFeatureCatalogTests
{
    private readonly DatabaseFixture _fx;

    public VenueFeatureCatalogTests(DatabaseFixture fx) => _fx = fx;

    private HttpClient Admin => _fx.CreateClientFor(_fx.AdminId, "super_admin");

    private static string Unique(string label) => $"{label} {Guid.NewGuid().ToString("N")[..6]}";

    private static async Task<List<VenueFeatureResponse>> ReadList(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<List<VenueFeatureResponse>>>())!.Data!;
    }

    private async Task<VenueFeatureResponse> CreateThrowaway(string icon = "equipment")
    {
        var res = await Admin.PostAsJsonAsync("/api/v1/venue-features", new
        {
            name = Unique("Throwaway"),
            nameAr = Unique("مؤقت"),
            icon,
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<VenueFeatureResponse>>())!.Data!;
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task Anonymous_SeesTheStarterCatalog_InDisplayOrder()
    {
        var list = await ReadList(await _fx.Factory.CreateClient().GetAsync("/api/v1/venue-features"));

        var parking = Assert.Single(list, f => f.Id == "vf-parking");
        Assert.Equal("Parking", parking.Name);
        // Asserted byte-for-byte: the Arabic seed went through a hand-written migration, and a
        // connection or column charset mistake would store "????" and still return 200.
        Assert.Equal("موقف سيارات", parking.NameAr);
        Assert.Equal("parking", parking.Icon);

        Assert.Equal(list.OrderBy(f => f.SortOrder).Select(f => f.Id), list.Select(f => f.Id));
        Assert.All(list, f => Assert.True(f.IsActive));
    }

    [Fact]
    public async Task Anonymous_IsNotToldHowManyVenuesUseEachFeature()
    {
        var res = await _fx.Factory.CreateClient().GetAsync("/api/v1/venue-features");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain("venueCount", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RetiredFeatures_AreHiddenFromEveryone_ButAdminsCanAskForThem()
    {
        var feature = await CreateThrowaway();
        var retire = await Admin.PatchAsJsonAsync($"/api/v1/venue-features/{feature.Id}", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, retire.StatusCode);

        var publicList = await ReadList(await _fx.Factory.CreateClient().GetAsync("/api/v1/venue-features"));
        Assert.DoesNotContain(publicList, f => f.Id == feature.Id);

        // includeInactive is an admin affordance: an owner passing it gets the active list.
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var ownerList = await ReadList(await owner.GetAsync("/api/v1/venue-features?includeInactive=true"));
        Assert.DoesNotContain(ownerList, f => f.Id == feature.Id);

        var adminList = await ReadList(await Admin.GetAsync("/api/v1/venue-features?includeInactive=true"));
        var retired = Assert.Single(adminList, f => f.Id == feature.Id);
        Assert.False(retired.IsActive);
        Assert.Equal(0, retired.VenueCount);
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public async Task Owner_CannotCreateACatalogFeature()
    {
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await owner.PostAsJsonAsync("/api/v1/venue-features",
            new { name = Unique("Owner Made"), nameAr = Unique("مالك"), icon = "wifi" });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_CreatesAFeature_WithAReadableStableId()
    {
        var name = Unique("Kids Zone");
        var res = await Admin.PostAsJsonAsync("/api/v1/venue-features",
            new { name, nameAr = Unique("منطقة أطفال"), icon = "kids_area" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var created = (await res.Content.ReadFromJsonAsync<ApiResponse<VenueFeatureResponse>>())!.Data!;
        Assert.StartsWith("vf-kids-zone-", created.Id);
        Assert.Equal(name, created.Name);
        Assert.Equal("kids_area", created.Icon);
        Assert.True(created.IsActive);
    }

    [Fact]
    public async Task Admin_CannotCreateADuplicateName_IgnoringCase()
    {
        var res = await Admin.PostAsJsonAsync("/api/v1/venue-features",
            new { name = "PARKING", nameAr = Unique("موقف"), icon = "parking" });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Theory]
    [InlineData("Valid Name", "", "wifi")]          // Arabic name missing
    [InlineData("", "اسم", "wifi")]                  // English name missing
    [InlineData("Valid Name", "اسم", "rocket")]      // not one of the fixed icon keys
    public async Task Admin_InvalidFields_Return400(string name, string nameAr, string icon)
    {
        var res = await Admin.PostAsJsonAsync("/api/v1/venue-features",
            new { name = name.Length == 0 ? "" : Unique(name), nameAr = nameAr.Length == 0 ? "" : Unique(nameAr), icon });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Renaming_KeepsTheId_SoVenuesAreNotOrphaned()
    {
        var feature = await CreateThrowaway();
        var renamed = Unique("Renamed");

        var res = await Admin.PatchAsJsonAsync($"/api/v1/venue-features/{feature.Id}", new { name = renamed });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<ApiResponse<VenueFeatureResponse>>())!.Data!;
        Assert.Equal(feature.Id, body.Id);
        Assert.Equal(renamed, body.Name);
    }

    // ------------------------------------------------------------------ deleting

    [Fact]
    public async Task Deleting_AFeatureInUse_IsRefused_AndItSurvives()
    {
        var feature = await CreateThrowaway();
        await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.FeatureIds = [feature.Id]);

        var res = await Admin.DeleteAsync($"/api/v1/venue-features/{feature.Id}");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("Deactivate", body!.Message);

        var adminList = await ReadList(await Admin.GetAsync("/api/v1/venue-features?includeInactive=true"));
        Assert.Equal(1, Assert.Single(adminList, f => f.Id == feature.Id).VenueCount);
    }

    [Fact]
    public async Task Deleting_AnUnusedFeature_RemovesIt()
    {
        var feature = await CreateThrowaway();

        var res = await Admin.DeleteAsync($"/api/v1/venue-features/{feature.Id}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var adminList = await ReadList(await Admin.GetAsync("/api/v1/venue-features?includeInactive=true"));
        Assert.DoesNotContain(adminList, f => f.Id == feature.Id);
    }
}
