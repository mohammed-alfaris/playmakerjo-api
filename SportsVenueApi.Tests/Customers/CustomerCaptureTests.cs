using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Customers;

/// <summary>
/// Customers are captured as a by-product of taking a booking — there is no "add customer"
/// path anywhere in the product, by design. These tests pin the capture rules: the customer
/// lands in the right owner's book, the same phone never creates two rows, and a player
/// cannot write into anyone's book at all.
/// </summary>
[Collection("Api")]
public class CustomerCaptureTests
{
    private readonly DatabaseFixture _fx;

    public CustomerCaptureTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(11).ToString("yyyy-MM-dd");

    private static object Manual(string venueId, string startTime, string? phone, string? name) => new
    {
        venueId,
        sport = "basketball",
        date = FutureDate,
        startTime,
        duration = 60,
        paymentMethod = "cliq",
        isManual = true,
        customerPhone = phone,
        customerName = name,
    };

    private async Task<Customer?> LoadCustomer(string ownerId, string phone)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.Phone == phone);
    }

    private async Task<int> CountCustomers(string ownerId, string phone)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Customers.CountAsync(c => c.OwnerId == ownerId && c.Phone == phone);
    }

    private async Task<string> Book(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/api/v1/bookings", body);
        res.EnsureSuccessStatusCode();
        var parsed = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        return parsed!.Data!.Id;
    }

    [Fact]
    public async Task WalkIn_WithPhone_CreatesCustomerUnderTheVenuesOwner()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var phone = "079" + Random.Shared.Next(1000000, 9999999);

        var bookingId = await Book(client, Manual(venue.Id, "10:00", phone, "خالد"));

        var customer = await LoadCustomer(_fx.OwnerAId, PhoneNormalizer.ToE164Jo(phone)!);
        Assert.NotNull(customer);
        Assert.Equal("خالد", customer!.Name);

        var booking = await _fx.LoadBooking(bookingId);
        Assert.Equal(customer.Id, booking!.CustomerId);
        // PlayerId is untouched and still non-null — the mobile app path must keep working.
        Assert.False(string.IsNullOrEmpty(booking.PlayerId));
    }

    [Fact]
    public async Task SamePhoneTwice_YieldsExactlyOneCustomer()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var phone = "078" + Random.Shared.Next(1000000, 9999999);
        var canonical = PhoneNormalizer.ToE164Jo(phone)!;

        // Typed differently the second time — exactly what happens in real life.
        await Book(client, Manual(venue.Id, "11:00", phone, "سامي"));
        await Book(client, Manual(venue.Id, "12:00", canonical, "سامي"));

        Assert.Equal(1, await CountCustomers(_fx.OwnerAId, canonical));
    }

    [Fact]
    public async Task ArabicIndicDigits_MatchTheSameCustomer()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        await Book(client, Manual(venue.Id, "13:00", "0791119999", "ليث"));
        await Book(client, Manual(venue.Id, "14:00", "٠٧٩١١١٩٩٩٩", "ليث"));

        Assert.Equal(1, await CountCustomers(_fx.OwnerAId, "+962791119999"));
    }

    [Fact]
    public async Task StoredName_IsNotOverwrittenByALaterBooking()
    {
        // The owner may have corrected the spelling in the customer sheet; a hurried
        // re-type at the counter must not undo that.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var phone = "077" + Random.Shared.Next(1000000, 9999999);

        await Book(client, Manual(venue.Id, "15:00", phone, "Ahmad Qasem"));
        await Book(client, Manual(venue.Id, "16:00", phone, "ahmad"));

        var customer = await LoadCustomer(_fx.OwnerAId, PhoneNormalizer.ToE164Jo(phone)!);
        Assert.Equal("Ahmad Qasem", customer!.Name);
    }

    [Fact]
    public async Task WalkIn_WithoutPhone_StillBooks_AndRecordsNoCustomer()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var bookingId = await Book(client, Manual(venue.Id, "17:00", null, "Cash guy"));

        var booking = await _fx.LoadBooking(bookingId);
        Assert.Null(booking!.CustomerId);
    }

    [Fact]
    public async Task UnusablePhone_DoesNotBlockTheBooking()
    {
        // A Syrian SIM or a landline must not stop the person in front of you booking.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var bookingId = await Book(client, Manual(venue.Id, "18:00", "+9715551234", "Tourist"));

        var booking = await _fx.LoadBooking(bookingId);
        Assert.Null(booking!.CustomerId);
    }

    [Fact]
    public async Task Player_CannotSmuggleAnArbitraryPhoneIntoTheBook()
    {
        // An app booking DOES create a customer — but from the player's own registered
        // phone, never from whatever they put in the request body. Otherwise any player
        // could write arbitrary rows into any owner's customer book.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var attackerPhone = "079" + Random.Shared.Next(1000000, 9999999);

        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = FutureDate,
            startTime = "19:00",
            duration = 60,
            paymentMethod = "cliq",
            customerPhone = attackerPhone,
            customerName = "Injected",
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(0, await CountCustomers(_fx.OwnerAId, PhoneNormalizer.ToE164Jo(attackerPhone)!));
    }

    [Fact]
    public async Task AppBooking_LinksToTheOwnersCustomerRecord()
    {
        // The seeded test player's own phone is what identifies them in the owner's book.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = FutureDate,
            startTime = "09:00",
            duration = 60,
            paymentMethod = "cliq",
        });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var booking = await _fx.LoadBooking(id);
        Assert.NotNull(booking!.CustomerId);
    }

    [Fact]
    public async Task SamePerson_ViaAppAndAtTheCounter_IsOneCustomer()
    {
        // The point of matching on phone: a regular who phones in June and switches to the
        // app in July is one person with a complete history — and does not wrongly appear
        // in the "stopped coming" list.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);

        // The seeded player's registered phone, typed by hand at the counter.
        const string playerPhone = "+962790000005";

        await Book(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"),
            Manual(venue.Id, "08:30", playerPhone, "Test Player"));

        var appRes = await _fx.CreateClientFor(_fx.PlayerId, "player")
            .PostAsJsonAsync("/api/v1/bookings", new
            {
                venueId = venue.Id,
                sport = "basketball",
                date = FutureDate,
                startTime = "09:30",
                duration = 60,
                paymentMethod = "cliq",
            });
        appRes.EnsureSuccessStatusCode();

        Assert.Equal(1, await CountCustomers(_fx.OwnerAId, playerPhone));
    }

    // ------------------------------------------------------------- paid vs pay-on-arrival

    [Fact]
    public async Task ManualBooking_PaidNow_IsRecordedAsSettled()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var id = await Book(client, Manual(venue.Id, "08:00", null, "Cash at counter"));

        var row = await _fx.LoadBooking(id);
        Assert.True(row!.DepositPaid);
        Assert.Equal(row.TotalAmount, row.AmountPaid, 3);
    }

    [Fact]
    public async Task ManualBooking_PayOnArrival_IsNotRecordedAsPaid()
    {
        // A Sunday phone call for a Tuesday slot. The pitch is held, the money is not in.
        // Recording this as settled is what used to hide the most expensive no-show there
        // is: booked, never paid, never turned up.
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = FutureDate,
            startTime = "22:00",
            duration = 60,
            paymentMethod = "cliq",
            isManual = true,
            customerPhone = "079" + Random.Shared.Next(1000000, 9999999),
            customerName = "Phone booking",
            customerPaid = false,
        });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var row = await _fx.LoadBooking(id);
        Assert.Equal("confirmed", row!.Status);   // the slot is still held
        Assert.False(row.DepositPaid);
        Assert.Equal(0, row.AmountPaid, 3);
    }

    [Fact]
    public async Task MarkPaid_SettlesAPayOnArrivalBooking()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = FutureDate,
            startTime = "21:30",
            duration = 60,
            paymentMethod = "cliq",
            isManual = true,
            customerPaid = false,
        });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!.Id;

        var paid = await client.PatchAsync($"/api/v1/bookings/{id}/mark-paid", null);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var row = await _fx.LoadBooking(id);
        Assert.True(row!.DepositPaid);
        Assert.Equal(row.TotalAmount, row.AmountPaid, 3);
    }

    [Fact]
    public async Task MarkPaid_IsRefusedOnACompetitorsBooking()
    {
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var id = await Book(_fx.CreateClientFor(_fx.OwnerBId, "venue_owner"),
            Manual(venueB.Id, "20:30", null, "Theirs"));

        var client = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await client.PatchAsync($"/api/v1/bookings/{id}/mark-paid", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task WriteStaff_CapturesIntoTheirEmployersBook_NotTheirOwn()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);
        var phone = "078" + Random.Shared.Next(1000000, 9999999);
        var canonical = PhoneNormalizer.ToE164Jo(phone)!;

        await Book(client, Manual(venue.Id, "20:00", phone, "Clerk's customer"));

        Assert.NotNull(await LoadCustomer(_fx.OwnerAId, canonical));
        Assert.Null(await LoadCustomer(_fx.StaffAWriteId, canonical));
    }

    [Fact]
    public async Task TwoOwners_KeepSeparateBooksForTheSamePhone()
    {
        // The same person plays at two different venues. Each owner keeps their own record;
        // neither can see the other's.
        var venueA = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var venueB = await _fx.CreateBasketballVenue(_fx.OwnerBId);
        var phone = "079" + Random.Shared.Next(1000000, 9999999);
        var canonical = PhoneNormalizer.ToE164Jo(phone)!;

        await Book(_fx.CreateClientFor(_fx.OwnerAId, "venue_owner"), Manual(venueA.Id, "21:00", phone, "Shared"));
        await Book(_fx.CreateClientFor(_fx.OwnerBId, "venue_owner"), Manual(venueB.Id, "21:00", phone, "Shared"));

        var a = await LoadCustomer(_fx.OwnerAId, canonical);
        var b = await LoadCustomer(_fx.OwnerBId, canonical);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(a!.Id, b!.Id);
    }
}
