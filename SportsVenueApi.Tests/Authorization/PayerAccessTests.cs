using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Auth;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// GAP-01 and GAP-02: two fixes that were each one expression wide, and each of which
/// silently disabled a whole role.
///
/// Both share a shape worth naming. A guard was written correctly — do not leak the CliQ
/// alias; do not trust the client about permissions — and then nobody asked the second
/// question: WHO legitimately needs this, and do they still get it? The alias fix closed
/// the anonymous leak and took the payment funnel with it. The login DTO was minimal and
/// left every counter clerk read-only.
///
/// <see cref="VenueDetailLeakTests"/> pins the half that must stay shut. This file pins the
/// half that must stay open, so neither can be re-broken in the name of the other.
/// </summary>
[Collection("Api")]
public class PayerAccessTests
{
    private readonly DatabaseFixture _fx;

    public PayerAccessTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(29).ToString("yyyy-MM-dd");

    private async Task<(string BookingId, string PlayerId, HttpClient Client)> BookAsPlayer(
        string venueId, string startTime)
    {
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId, sport = "basketball", date = FutureDate,
            startTime, duration = 60, paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        return (body!.Data!.Id, player.Id, client);
    }

    // -----------------------------------------------------------------------------------
    // GAP-01 — the player who must pay can see the alias
    // -----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePlayerWhoBookedCanSeeTheAliasTheyMustPayTo()
    {
        // Without this the app renders its placeholder string where the alias belongs, the
        // customer cannot transfer, and the unpaid-booking sweep eventually cancels a
        // booking that was never payable. The security fix caused a payment outage.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "payme@cliq");

        var (_, _, client) = await BookAsPlayer(venue.Id, "09:00");

        var res = await client.GetAsync("/api/v1/bookings/my");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var mine = (await res.Content.ReadFromJsonAsync<ApiResponse<List<BookingResponse>>>())!.Data!;

        Assert.Equal("payme@cliq", Assert.Single(mine).Venue.CliqAlias);
    }

    [Fact]
    public async Task TheAliasIsOnTheBookingTheMomentItIsCreated()
    {
        // The confirmation screen renders straight off the create response.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "instant@cliq");
        var player = await _fx.CreatePlayer();

        var res = await _fx.CreateClientFor(player.Id, "player").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = FutureDate,
            startTime = "10:00", duration = 60, paymentMethod = "cliq",
        });

        var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!;
        Assert.Equal("instant@cliq", dto.Venue.CliqAlias);
    }

    [Fact]
    public async Task ADifferentPlayerStillCannotSeeIt()
    {
        // The grant is a relationship to one booking, not a role. Widening CanView to
        // include players would have handed them back-office reads everywhere else.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "notyours@cliq");
        var (bookingId, _, _) = await BookAsPlayer(venue.Id, "11:00");

        var stranger = await _fx.CreatePlayer();
        var res = await _fx.CreateClientFor(stranger.Id, "player").GetAsync($"/api/v1/bookings/{bookingId}");

        // Someone else's booking is not theirs to read at all.
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task TheAnonymousVenueRouteIsStillClosed()
    {
        // The original leak. Fixing the funnel must not reopen the harvest: venue ids come
        // free from the public list, so an open route means every alias on the platform.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "stillshut@cliq");
        await BookAsPlayer(venue.Id, "12:00");

        var res = await _fx.Factory.CreateClient().GetAsync($"/api/v1/venues/public/{venue.Id}");
        var dto = (await res.Content.ReadFromJsonAsync<ApiResponse<VenueResponse>>())!.Data!;

        Assert.Null(dto.CliqAlias);
    }

    // -----------------------------------------------------------------------------------
    // GAP-02 — login tells the dashboard what the staff member may do
    // -----------------------------------------------------------------------------------

    /// <summary>
    /// The ONLY tests in the suite that go through /auth/login, and they have to: the whole
    /// finding is about what the login response omits, so minting a token directly through
    /// TestAuthHelper would test nothing.
    ///
    /// Auth endpoints are rate-limited to 5 requests a minute, which is why every other test
    /// mints tokens instead. Keep the call count in this class to a strict minimum — one
    /// login per assertion group, never one per assertion — or the class starts going red on
    /// a 429 that has nothing to do with the code under test.
    /// </summary>
    private async Task<AuthUserResponse> LoginAs(string email)
    {
        var res = await _fx.Factory.CreateClient()
            .PostAsJsonAsync("/api/v1/auth/login", new { email, password = DatabaseFixture.TestPassword });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<LoginData>>())!.Data!.User;
    }

    [Fact]
    public async Task LoginTellsAWriteClerkTheyMayWrite()
    {
        // The dashboard stores this response and never re-fetches the profile, so an
        // omitted field is permanent: useRole fell back to "read" and hid every action
        // button from the exact role the feature was built for.
        var user = await LoginAs("staff-a-write@test.local");

        Assert.Equal("venue_staff", user.Role);
        Assert.Equal("write", user.Permissions);
        Assert.Equal(_fx.OwnerAId, user.ManagedByOwnerId);
    }

    [Fact]
    public async Task LoginTellsAReadOnlyClerkTheyMayNot()
    {
        var user = await LoginAs("staff-a-read@test.local");

        Assert.Equal("read", user.Permissions);
    }

    [Fact]
    public async Task OwnersAndAdminsCarryNoPermissionsField()
    {
        // The column is only maintained for staff. Emitting a value for other roles would
        // invite a client to start branching on it where it means nothing.
        //
        // One login per user, both fields asserted off the same response — see LoginAs.
        var owner = await LoginAs("owner-a@test.local");
        Assert.Null(owner.Permissions);
        Assert.Null(owner.ManagedByOwnerId);

        Assert.Null((await LoginAs("admin@test.local")).Permissions);
    }
}
