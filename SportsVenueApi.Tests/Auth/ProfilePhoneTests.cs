using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Auth;

/// <summary>
/// PATCH /users/me wrote the phone straight through — trimmed, unvalidated, not normalised,
/// and with no uniqueness check. That field is not decoration: an app booking resolves the
/// venue's customer record from it.
///
/// So the phone was an identity key that any account could set to any value. Type a regular's
/// number (it is on the venue's Instagram), book a slot, and your bookings merge into that
/// person's history at the venue — and a later no-show lands on THEIR reliability badge, the
/// one the owner reads at the counter before handing over a pitch.
///
/// Two things stop it: the number is validated and canonicalised on the way in so it cannot
/// be claimed twice, and the customer record is no longer echoed back to the player who
/// triggered the match.
/// </summary>
[Collection("Api")]
public class ProfilePhoneTests
{
    private readonly DatabaseFixture _fx;

    public ProfilePhoneTests(DatabaseFixture fx) => _fx = fx;

    private async Task<HttpResponseMessage> SetPhone(string userId, string phone)
    {
        var client = _fx.CreateClientFor(userId, "player");
        return await client.PatchAsJsonAsync("/api/v1/users/me", new { phone });
    }

    [Fact]
    public async Task ANumberAlreadyHeldByAnotherUser_IsRefused()
    {
        var victim = await _fx.CreatePlayer();
        var attacker = await _fx.CreatePlayer();
        await SetPhone(victim.Id, "0791200001");

        var res = await SetPhone(attacker.Id, "0791200001");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task TheSameNumberInADifferentSpelling_IsAlsoRefused()
    {
        // Canonicalising before the comparison is the point: without it "0791200002" and
        // "+962791200002" are two rows and the uniqueness check is decorative.
        var victim = await _fx.CreatePlayer();
        var attacker = await _fx.CreatePlayer();
        await SetPhone(victim.Id, "0791200002");

        var res = await SetPhone(attacker.Id, "+962791200002");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AnUnparseableNumber_IsRefused()
    {
        var player = await _fx.CreatePlayer();

        var res = await SetPhone(player.Id, "not-a-phone");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task AFreeNumber_IsAcceptedAndStoredCanonically()
    {
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");

        var res = await client.PatchAsJsonAsync("/api/v1/users/me", new { phone = "0791200003" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>();
        Assert.Equal("+962791200003", body!.Data!.Phone);
    }

    [Fact]
    public async Task EditingTheNameAlone_DoesNotTripThePhoneRules()
    {
        // The dashboard PATCHes the whole profile back. A user whose stored number predates
        // this validation must still be able to change their name.
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");

        var res = await client.PatchAsJsonAsync("/api/v1/users/me",
            new { name = "Renamed", phone = player.Phone });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // ------------------------------------------- the other half: don't echo the match back

    [Fact]
    public async Task APlayersBookingResponse_DoesNotCarryTheVenuesCustomerRecord()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);

        // The owner takes a counter booking, which creates the customer record.
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var counter = await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = PlatformConstants.JordanToday().AddDays(15).ToString("yyyy-MM-dd"),
            startTime = "11:00",
            duration = 60,
            paymentMethod = "cliq",
            isManual = true,
            customerPhone = "0791200009",
            customerName = "Khalid The Regular",
        });
        Assert.Equal(HttpStatusCode.OK, counter.StatusCode);

        // A player claims that number and books. The claim is refused outright now, but even
        // if a number were reused legitimately, the response must not name the customer.
        var attacker = await _fx.CreatePlayer();
        await SetPhone(attacker.Id, "0791200009");
        var client = _fx.CreateClientFor(attacker.Id, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = PlatformConstants.JordanToday().AddDays(15).ToString("yyyy-MM-dd"),
            startTime = "14:00",
            duration = 60,
            paymentMethod = "cliq",
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var booking = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data;
        Assert.Null(booking!.Customer);
    }

    [Fact]
    public async Task TheOwnerStillSeesTheCustomerOnHisOwnBookings()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = PlatformConstants.JordanToday().AddDays(15).ToString("yyyy-MM-dd"),
            startTime = "16:00",
            duration = 60,
            paymentMethod = "cliq",
            isManual = true,
            customerPhone = "0791200010",
            customerName = "Counter Customer",
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var booking = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data;
        // Stripping this for players must not blank the name the counter screen is built on.
        Assert.Equal("Counter Customer", booking!.Customer!.Name);
    }
}
