using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// The CliQ alias is the string a customer transfers money to. It is not a password — a
/// paying customer must see it — but "visible to someone who is paying you" and "harvestable
/// in bulk for every venue on the platform" are different things, and the second is what is
/// needed to impersonate a venue and substitute a different alias.
///
/// It was reachable with NO authentication at all: /venues/public and /venues/public/{id}
/// returned the full owner DTO, venue ids are enumerable from the list route, and
/// GET /venues/{id} had no ownership check whatsoever — so a competing owner could read a
/// rival's pricing, deposit percentage and alias by id.
///
/// The earlier isolation work fixed the LIST and the reports and never touched these four
/// routes. That is the failure these tests exist to stop repeating: a leak that survived
/// because nobody asked the question about one particular endpoint.
/// </summary>
[Collection("Api")]
public class VenueDetailLeakTests
{
    private readonly DatabaseFixture _fx;

    public VenueDetailLeakTests(DatabaseFixture fx) => _fx = fx;

    private async Task<VenueResponse?> GetVenue(HttpClient client, string path)
    {
        var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<VenueResponse>>())!.Data;
    }

    [Fact]
    public async Task TheAnonymousVenuePageDoesNotCarryTheCliqAlias()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "leak-check@cliq");
        var anonymous = _fx.Factory.CreateClient();

        var dto = await GetVenue(anonymous, $"/api/v1/venues/public/{venue.Id}");

        Assert.NotNull(dto);
        Assert.Null(dto!.CliqAlias);
        // Everything a player needs to decide and book must survive the strip.
        Assert.Equal(venue.PricePerHour, dto.PricePerHour);
        Assert.Equal(venue.DepositPercentage, dto.DepositPercentage);
    }

    [Fact]
    public async Task TheAnonymousVenueListDoesNotCarryAnyCliqAlias()
    {
        await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "list-leak@cliq");
        var anonymous = _fx.Factory.CreateClient();

        var res = await anonymous.GetAsync("/api/v1/venues/public?limit=100");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<ApiResponse<List<VenueResponse>>>())!.Data!;

        Assert.NotEmpty(body);
        Assert.All(body, v => Assert.Null(v.CliqAlias));
    }

    [Fact]
    public async Task ACompetingOwnerCannotReadAnothersAliasByVenueId()
    {
        // The one that mattered most: ids come free from the public list, and every owner
        // on the platform holds a valid token.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "rival@cliq");
        var rival = _fx.CreateClientFor(_fx.OwnerBId, "venue_owner");

        var dto = await GetVenue(rival, $"/api/v1/venues/{venue.Id}");

        Assert.Null(dto!.CliqAlias);
    }

    [Fact]
    public async Task APlayerSeesEnoughToBookButNotTheAlias()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "player-view@cliq");
        var player = await _fx.CreatePlayer();

        var dto = await GetVenue(_fx.CreateClientFor(player.Id, "player"), $"/api/v1/venues/{venue.Id}");

        // Not 403: opening a venue page to book it is exactly what a player should do.
        // What changes is the shape they get back.
        Assert.Null(dto!.CliqAlias);
        Assert.Equal(venue.Name, dto.Name);
        Assert.NotNull(dto.OperatingHours);
    }

    [Fact]
    public async Task TheOwnerStillSeesHisOwnAlias()
    {
        // The strip must not blind the owner to his own settings screen.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "mine@cliq");

        var dto = await GetVenue(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"), $"/api/v1/venues/{venue.Id}");

        Assert.Equal("mine@cliq", dto!.CliqAlias);
    }

    [Fact]
    public async Task StaffSeeTheirEmployersAlias()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "employer@cliq");
        var staff = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        var dto = await GetVenue(staff, $"/api/v1/venues/{venue.Id}");

        // They take payments at the counter; the alias is part of doing that job.
        Assert.Equal("employer@cliq", dto!.CliqAlias);
    }

    [Fact]
    public async Task StaffOfAnotherOwnerDoNot()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "not-yours@cliq");
        var otherStaff = await _fx.CreateClientForUserAsync(_fx.StaffBWriteId);

        var dto = await GetVenue(otherStaff, $"/api/v1/venues/{venue.Id}");

        Assert.Null(dto!.CliqAlias);
    }

    // ------------------------------------------------- the same alias, via the booking DTO
    //
    // Closing the venue routes left the alias on the venue stub nested inside every booking
    // response, where nothing checked the caller at all. That reopened the whole leak through
    // a different door, and at lower cost to the attacker: venue ids come from the anonymous
    // list, POST /bookings has no ownership check on the player path, so a free account could
    // mint a booking against any venue, read the alias off the response, and cancel.

    private static object PlayerBooking(string venueId, string startTime) => new
    {
        venueId,
        sport = "basketball",
        date = PlatformConstants.JordanToday().AddDays(13).ToString("yyyy-MM-dd"),
        startTime,
        duration = 60,
        paymentMethod = "cliq",
    };

    private async Task<BookingResponse?> Book(HttpClient client, string venueId, string startTime)
    {
        var res = await client.PostAsJsonAsync("/api/v1/bookings", PlayerBooking(venueId, startTime));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data;
    }

    [Fact]
    public async Task APlayersOwnBookingDoesNotCarryTheVenuesAlias()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "booking-leak@cliq");
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");

        var booking = await Book(client, venue.Id, "09:00");

        Assert.NotNull(booking);
        Assert.Null(booking!.Venue.CliqAlias);
    }

    [Fact]
    public async Task ARivalOwnerCannotHarvestTheAliasByBookingTheVenue()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "rival-harvest@cliq");
        var rival = _fx.CreateClientFor(_fx.OwnerBId, "venue_owner");

        var booking = await Book(rival, venue.Id, "09:30");

        Assert.NotNull(booking);
        Assert.Null(booking!.Venue.CliqAlias);
    }

    [Fact]
    public async Task TheOwnerStillSeesTheAliasOnHisOwnBookings()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v => v.CliqAlias = "mine-booking@cliq");
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var booking = await Book(owner, venue.Id, "10:30");

        // The strip must not break the screen that shows the owner where money arrives.
        Assert.Equal("mine-booking@cliq", booking!.Venue.CliqAlias);
    }
}
