using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

/// <summary>
/// A pitch size is a football concept — "7" is shorthand for seven-a-side, and the dashboard
/// renders it as the badge "7v7" on the booking block.
///
/// The booking DTO used to fill a missing size from <c>Venue.ParentSize</c>, which is the
/// venue's FOOTBALL size, without asking which pitch the booking was on. On a venue that has
/// both a football pitch and a padel court, every padel booking came back carrying the
/// football size and the timeline drew "7v7" on the padel court.
///
/// The same expression also feeds <see cref="SportsVenueApi.Constants.PitchSizes.WeightOf"/>
/// on the availability route, where the borrowed size is not merely a wrong label — it is
/// 2 capacity units for a "7" venue (4 for an "11" one) against a real cost of 1.
/// </summary>
[Collection("Api")]
public class PitchSizeLabelTests
{
    private readonly DatabaseFixture _fx;

    public PitchSizeLabelTests(DatabaseFixture fx) => _fx = fx;

    /// <summary>A venue with a subdividable football pitch AND a padel court.</summary>
    private async Task<(Venue Venue, string FootballPitchId, string PadelPitchId)> MixedVenue()
    {
        var football = "p-" + Guid.NewGuid().ToString("N")[..8];
        var padel = "p-" + Guid.NewGuid().ToString("N")[..8];
        var venue = new Venue
        {
            Name = "Mixed Venue", OwnerId = _fx.OwnerAId,
            Sports = ["football", "padel"], City = "Amman", Address = "Mixed Street",
            PricePerHour = 40, DepositPercentage = 20, Status = "active",
            CliqAlias = "mixed@cliq", OperatingHours = TestEntities.DailyHours(),
            // The venue-level legacy field — the football size, and the value that used to
            // leak onto every padel booking.
            ParentSize = "7",
            Pitches =
            [
                new PitchDto
                {
                    Id = football, Name = "Football Pitch", Sport = "football",
                    ParentSize = "7", SubSizes = ["5"],
                    SizePrices = new() { ["5"] = 20 }, PricePerHour = 40
                },
                new PitchDto
                {
                    Id = padel, Name = "Padel Court 1", Sport = "padel",
                    // A padel court has no a-side size. This is what ResolvedPitches
                    // already gets right, and what the DTO used to ignore.
                    ParentSize = null, PricePerHour = 25
                }
            ]
        };
        await _fx.Insert(venue);
        return (venue, football, padel);
    }

    private async Task<string> Book(string venueId, string pitchId, string sport, string start)
    {
        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await owner.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId,
            pitchId,
            sport,
            date = PlatformConstants.JordanToday().AddDays(3).ToString("yyyy-MM-dd"),
            startTime = start,
            duration = 60,
            isManual = true,
            customerPhone = "0791234567",
            customerName = "Test Customer",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;
    }

    [Fact]
    public async Task APadelBookingDoesNotCarryTheVenuesFootballSize()
    {
        // The reported bug, exactly: a padel court showing the "7v7" badge.
        var (venue, _, padelId) = await MixedVenue();
        var bookingId = await Book(venue.Id, padelId, "padel", "10:00");

        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var dto = (await owner.GetFromJsonAsync<ApiResponse<BookingResponse>>(
            $"/api/v1/bookings/{bookingId}"))!.Data!;

        Assert.Null(dto.PitchSize);
    }

    [Fact]
    public async Task AFootballBookingStillReportsItsSize()
    {
        // The other half. Nulling the fallback outright would have passed the test above
        // while stripping the badge off every football booking that predates per-booking
        // sizes — a silent regression the padel assertion cannot see.
        var (venue, footballId, _) = await MixedVenue();
        var bookingId = await Book(venue.Id, footballId, "football", "12:00");

        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var dto = (await owner.GetFromJsonAsync<ApiResponse<BookingResponse>>(
            $"/api/v1/bookings/{bookingId}"))!.Data!;

        Assert.Equal("7", dto.PitchSize);
    }

    [Fact]
    public async Task ALegacySinglePitchVenueStillReportsTheVenueLevelSize()
    {
        // A venue with no pitches array at all: here the venue-level field genuinely IS
        // that one pitch's size, so the fallback must survive. This is the case that makes
        // resolving by pitch-id correct rather than just deleting the fallback.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId, v =>
        {
            v.Sports = ["football"];
            v.ParentSize = "11";
        });
        var bookingId = await Book(venue.Id, null!, "football", "14:00");

        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var dto = (await owner.GetFromJsonAsync<ApiResponse<BookingResponse>>(
            $"/api/v1/bookings/{bookingId}"))!.Data!;

        Assert.Equal("11", dto.PitchSize);
    }

    [Fact]
    public async Task APadelBookingWeighsOneUnitNotTwo()
    {
        // The consequence that is not cosmetic. WeightOf("7") is 2 (UnitWeight, PitchSizes.cs:27),
        // so before the fix a single padel booking ate two capacity units instead of one — and
        // four on a venue whose ParentSize is "11".
        var (venue, _, padelId) = await MixedVenue();
        await Book(venue.Id, padelId, "padel", "16:00");

        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var date = PlatformConstants.JordanToday().AddDays(3).ToString("yyyy-MM-dd");
        var doc = await owner.GetFromJsonAsync<System.Text.Json.JsonDocument>(
            $"/api/v1/venues/{venue.Id}/available-slots?date={date}");

        var slots = doc!.RootElement.GetProperty("data").GetProperty("bookedSlots");
        var padelSlot = slots.EnumerateArray().Single(s =>
            s.GetProperty("startTime").GetString() == "16:00");

        Assert.Equal(1, padelSlot.GetProperty("unitWeight").GetInt32());
    }
}
