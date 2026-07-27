using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// venue_staff existed as a role but was linked to nothing, so the server could not answer
/// "which venue does this person work for". The result was inverted: staff were blocked from
/// the one thing a counter clerk exists to do (take a walk-in booking) while passing every
/// deny-list guard on the endpoints that change booking state, across all owners.
///
/// These tests pin both halves — staff can now do their job on their employer's venues, and
/// only there. Every client here is minted from the user's real database row, so a token no
/// real login could issue cannot make a test pass.
/// </summary>
[Collection("Api")]
public class StaffAccessTests
{
    private readonly DatabaseFixture _fx;

    public StaffAccessTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(9).ToString("yyyy-MM-dd");

    private static object ManualBooking(string venueId, string startTime) => new
    {
        venueId,
        sport = "basketball",
        date = FutureDate,
        startTime,
        duration = 60,
        paymentMethod = "cliq",
        isManual = true,
    };

    // ------------------------------------------------- the counter clerk can do their job

    [Fact]
    public async Task WriteStaff_CanCreateManualBooking_OnTheirOwnersVenue()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        var res = await client.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venue.Id, "10:00"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        var row = await _fx.LoadBooking(body!.Data!.Id);
        Assert.Equal("confirmed", row!.Status);
        Assert.Equal(0, row.SystemFee, 3);   // manual bookings stay commission-free
    }

    [Fact]
    public async Task ReadStaff_CannotCreateManualBooking()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = await _fx.CreateClientForUserAsync(_fx.StaffAReadId);

        var res = await client.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venue.Id, "11:00"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task WriteStaff_CannotCreateManualBooking_OnACompetitorsVenue()
    {
        var competitorVenue = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        var res = await client.PostAsJsonAsync("/api/v1/bookings", ManualBooking(competitorVenue.Id, "12:00"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task UnlinkedStaff_CannotCreateManualBooking()
    {
        // Legacy rows have no owner. Null must mean "nothing", never "everything".
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = await _fx.CreateClientForUserAsync(_fx.StaffUnlinkedId);

        var res = await client.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venue.Id, "13:00"));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ----------------------------------------------------------------- reading the diary

    [Fact]
    public async Task Staff_SeeTheirOwnersBookings_AndNotACompetitors()
    {
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);

        var ownerA = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var mine = await ownerA.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venueA.Id, "14:00"));
        var mineId = (await mine.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var ownerB = _fx.CreateClientFor(_fx.OwnerBId, "venue_owner");
        var theirs = await ownerB.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venueB.Id, "15:00"));
        var theirsId = (await theirs.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        // Read-only staff still get the schedule — that is the point of "read".
        var client = await _fx.CreateClientForUserAsync(_fx.StaffAReadId);
        var res = await client.GetAsync("/api/v1/bookings?limit=200");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var payload = await res.Content.ReadAsStringAsync();
        Assert.Contains(mineId, payload);
        Assert.DoesNotContain(theirsId, payload);
    }

    [Fact]
    public async Task UnlinkedStaff_GetsNothingFromTheBookingsList()
    {
        var client = await _fx.CreateClientForUserAsync(_fx.StaffUnlinkedId);
        var res = await client.GetAsync("/api/v1/bookings?limit=200");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Staff_CannotSpoofScopeWithOwnerIdQueryParam()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var ownerB = _fx.CreateClientFor(_fx.OwnerBId, "venue_owner");
        var res0 = await ownerB.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venueB.Id, "16:00"));
        var theirsId = (await res0.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var res = await client.GetAsync($"/api/v1/bookings?limit=200&owner_id={_fx.OwnerBId}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain(theirsId, await res.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------- mutating existing bookings

    [Fact]
    public async Task WriteStaff_CanCompleteABooking_OnTheirOwnersVenue()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var created = await owner.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venue.Id, "17:00"));
        var id = (await created.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var res = await client.PatchAsync($"/api/v1/bookings/{id}/complete", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("completed", (await _fx.LoadBooking(id))!.Status);
    }

    [Fact]
    public async Task ReadStaff_CannotCompleteABooking()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var created = await owner.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venue.Id, "18:00"));
        var id = (await created.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAReadId);
        var res = await client.PatchAsync($"/api/v1/bookings/{id}/complete", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("confirmed", (await _fx.LoadBooking(id))!.Status);
    }

    [Fact]
    public async Task Staff_CannotTouchACompetitorsBooking()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var ownerB = _fx.CreateClientFor(_fx.OwnerBId, "venue_owner");
        var created = await ownerB.PostAsJsonAsync("/api/v1/bookings", ManualBooking(venueB.Id, "19:00"));
        var id = (await created.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/bookings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsync($"/api/v1/bookings/{id}/complete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsync($"/api/v1/bookings/{id}/no-show", null)).StatusCode);
        Assert.Equal("confirmed", (await _fx.LoadBooking(id))!.Status);
    }

    [Fact]
    public async Task Staff_SeeOnlyTheirOwnersVenues()
    {
        var mine = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.Name = "Staff Venue Mine");
        var theirs = await _fx.CreateBasketballVenue(_fx.OwnerBId, v => v.Name = "Staff Venue Theirs");

        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var res = await client.GetAsync("/api/v1/venues?limit=200");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var payload = await res.Content.ReadAsStringAsync();
        Assert.Contains(mine.Id, payload);
        Assert.DoesNotContain(theirs.Id, payload);
    }

    // ----------------------------------------------------------- staff are not managers

    [Theory]
    [InlineData("summary")]
    [InlineData("revenue-chart")]
    [InlineData("top-venues")]
    [InlineData("sports-breakdown")]
    [InlineData("export")]
    public async Task Staff_CannotReadReports_EvenWithWrite(string endpoint)
    {
        // "write" lets a clerk take a booking. It must never surface the owner's revenue.
        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var res = await client.GetAsync($"/api/v1/reports/{endpoint}");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Staff_CannotCreateMoreStaff()
    {
        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var res = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Sneaky Staff",
            email = $"sneaky-{Guid.NewGuid():N}@test.local",
            password = "Test#12345",
            role = "venue_staff",
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // --------------------------------------------------------------- creating staff rows

    [Fact]
    public async Task OwnerCreatedStaff_IsLinkedToThatOwner()
    {
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "New Clerk",
            email = $"clerk-{Guid.NewGuid():N}@test.local",
            password = "Test#12345",
            role = "venue_staff",
            permissions = "write",
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;
        Assert.Equal(_fx.OwnerAId, dto.ManagedByOwnerId);
        Assert.Equal("write", dto.Permissions);
    }

    [Fact]
    public async Task Owner_CannotPlantStaffInsideACompetitorsAccount()
    {
        // managedByOwnerId is ignored for owners — they always get themselves.
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Trojan Clerk",
            email = $"trojan-{Guid.NewGuid():N}@test.local",
            password = "Test#12345",
            role = "venue_staff",
            permissions = "write",
            managedByOwnerId = _fx.OwnerBId,
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!;
        Assert.Equal(_fx.OwnerAId, dto.ManagedByOwnerId);
    }

    [Fact]
    public async Task Admin_MustNameTheOwnerWhenCreatingStaff()
    {
        // Otherwise the account is born unlinked and silently able to do nothing.
        var client = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var res = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Orphan Clerk",
            email = $"orphan-{Guid.NewGuid():N}@test.local",
            password = "Test#12345",
            role = "venue_staff",
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Admin_CannotLinkStaffToANonOwner()
    {
        var client = _fx.CreateClientFor(_fx.AdminId, "super_admin");
        var res = await client.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Bad Link Clerk",
            email = $"badlink-{Guid.NewGuid():N}@test.local",
            password = "Test#12345",
            role = "venue_staff",
            managedByOwnerId = _fx.PlayerId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
