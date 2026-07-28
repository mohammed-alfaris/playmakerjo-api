using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Venues;

/// <summary>
/// Deleting a pitch that still has bookings on it sells the same hour twice.
///
/// Every conflict filter matches a booking to a pitch by exact id, and the legacy
/// "first pitch of this sport" fallback fires only for a NULL pitch_id — never for a
/// dangling one. So the moment a pitch is removed from the venue's JSON, every booking
/// pointing at it stops matching any pitch, drops out of every capacity sum, and the hour
/// it occupies is offered for sale again while the customer still holds it.
///
/// It is reachable with two clicks: the pitch card has a delete button, and the venue form
/// resends the whole array on save.
/// </summary>
[Collection("Api")]
public class PitchRemovalTests
{
    private readonly DatabaseFixture _fx;

    public PitchRemovalTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(31).ToString("yyyy-MM-dd");

    /// <summary>The venue's pitch array minus one pitch, in the shape PATCH expects.</summary>
    private static object PatchWithout(Venue venue, string dropPitchId) => new
    {
        pitches = venue.Pitches!
            .Where(p => p.Id != dropPitchId)
            .Select(p => new
            {
                id = p.Id, name = p.Name, sport = p.Sport,
                parentSize = p.ParentSize, subSizes = p.SubSizes,
                sizePrices = p.SizePrices, pricePerHour = p.PricePerHour,
            })
            .ToList(),
    };

    [Fact]
    public async Task RemovingAPitchThatStillHasAnUpcomingBookingIsRefused()
    {
        var (venue, pitchId) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var booked = await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "football", pitchSize = "6",
            date = FutureDate, startTime = "18:00", duration = 60,
            paymentMethod = "cliq", isManual = true, customerPaid = true,
            customerPhone = "0791234901", customerName = "خالد النتور",
        });
        Assert.Equal(HttpStatusCode.OK, booked.StatusCode);

        var res = await owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", PatchWithout(venue, pitchId));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("upcoming booking", body!.Message);

        // And the pitch is still there — a refused edit must not half-apply.
        var after = await _fx.LoadVenue(venue.Id);
        Assert.Contains(after!.Pitches!, p => p.Id == pitchId);
    }

    [Fact]
    public async Task TheBookingStillBlocksItsSlotAfterTheRefusedRemoval()
    {
        // The point of the guard. If the removal had gone through, this second attempt
        // would succeed and two customers would hold the same hour.
        var (venue, pitchId) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        object Booking(string phone) => new
        {
            venueId = venue.Id, sport = "football", pitchSize = "11",
            date = FutureDate, startTime = "19:00", duration = 60,
            paymentMethod = "cliq", isManual = true, customerPaid = true,
            customerPhone = phone, customerName = "زبون",
        };

        Assert.Equal(HttpStatusCode.OK,
            (await owner.PostAsJsonAsync("/api/v1/bookings", Booking("0791234902"))).StatusCode);

        await owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", PatchWithout(venue, pitchId));

        // A full-size pitch is fully consumed, so the second attempt must still be refused.
        var second = await owner.PostAsJsonAsync("/api/v1/bookings", Booking("0791234903"));
        Assert.NotEqual(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task APitchWithOnlyPastBookingsCanStillBeRetired()
    {
        // Retiring a pitch has to stay possible once its diary is clear. Past bookings keep
        // their dead pitch id — they are history and no longer contend for a slot.
        var (venue, pitchId) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();

        await _fx.Insert(new Booking
        {
            VenueId = venue.Id, PlayerId = player.Id, Sport = "football",
            PitchId = pitchId, PitchSize = "6",
            Date = PlatformConstants.JordanToday().AddDays(-40), StartTime = "18:00", Duration = 60,
            Amount = 10, TotalAmount = 10, DepositAmount = 2,
            Status = "completed", PaymentMethod = "cliq",
        });

        var res = await _fx.CreateClientFor(_fx.OwnerAId, "venue_owner")
            .PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", PatchWithout(venue, pitchId));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain((await _fx.LoadVenue(venue.Id))!.Pitches!, p => p.Id == pitchId);
    }

    [Fact]
    public async Task ACancelledBookingDoesNotBlockRetiringThePitch()
    {
        var (venue, pitchId) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var booked = await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "football", pitchSize = "6",
            date = FutureDate, startTime = "20:00", duration = 60,
            paymentMethod = "cliq", isManual = true, customerPaid = true,
            customerPhone = "0791234904", customerName = "زبون ملغي",
        });
        var id = (await booked.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
        await owner.PatchAsync($"/api/v1/bookings/{id}/cancel", null);

        var res = await owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", PatchWithout(venue, pitchId));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task EditingAVenueWithoutTouchingItsPitchesIsUnaffected()
    {
        // The form resends the whole pitch array on every save, so the guard must not turn
        // an ordinary rename into a 409.
        var (venue, _) = await _fx.CreateSubdividableVenue(_fx.OwnerAId);
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "football", pitchSize = "6",
            date = FutureDate, startTime = "21:00", duration = 60,
            paymentMethod = "cliq", isManual = true, customerPaid = true,
            customerPhone = "0791234905", customerName = "زبون",
        });

        var res = await owner.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new
        {
            name = "Renamed Venue",
            pitches = venue.Pitches!.Select(p => new
            {
                id = p.Id, name = p.Name, sport = p.Sport,
                parentSize = p.ParentSize, subSizes = p.SubSizes,
                sizePrices = p.SizePrices, pricePerHour = p.PricePerHour,
            }).ToList(),
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
