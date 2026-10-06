using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Web;
using SportsVenueApi.Helpers;
using SportsVenueApi.Jobs;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Web;

/// <summary>
/// The venue's public booking link: a guest with a name and a phone either sends a request the
/// venue confirms, or pays the deposit now and uploads the transfer — the slot held until the
/// venue reviews it. The guest has no account, so nothing addressed to "the player" is sent,
/// and the same slot rules as every other booking apply.
/// </summary>
[Collection("Api")]
public class WebBookingTests
{
    private readonly DatabaseFixture _fx;
    private readonly HttpClient _guest;

    public WebBookingTests(DatabaseFixture fx)
    {
        _fx = fx;
        _guest = fx.Factory.CreateClient();
    }

    private static string Date(int days) => PlatformConstants.JordanToday().AddDays(days).ToString("yyyy-MM-dd");

    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0, 0x90, 0x77, 0x53, 0xDE,
    ];

    private async Task<(User Owner, Venue Venue)> Venue(Action<Venue>? mutate = null)
    {
        var owner = await _fx.CreateOwner();
        var venue = await _fx.CreateBasketballVenue(owner.Id, mutate);
        return (owner, venue);
    }

    private async Task<HttpResponseMessage> Request(Venue venue, bool payNow = false, string time = "18:00", string phone = "0791234567", int days = 5, string? website = null) =>
        await _guest.PostAsJsonAsync($"/api/v1/public/v/{venue.Id}/requests", new
        {
            sport = "basketball", date = Date(days), startTime = time, duration = 60,
            name = "Web Guest", phone, payNow, website,
        });

    private async Task<string> Token(Venue venue, bool payNow = false, string time = "18:00", string phone = "0791234567")
    {
        var res = await Request(venue, payNow, time, phone);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<WebBookingCreated>>())!.Data!.Token;
    }

    private async Task<WebBookingStatus> Status(string token) =>
        (await (await _guest.GetAsync($"/api/v1/public/requests/{token}")).Content.ReadFromJsonAsync<ApiResponse<WebBookingStatus>>())!.Data!;

    private async Task<Booking> Row(string token)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Bookings.AsNoTracking().FirstAsync(b => b.PublicToken == token);
    }

    private async Task<List<(string Type, string? Ref)>> Inbox(string userId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Notifications.Where(n => n.UserId == userId).Select(n => new { n.Type, n.ReferenceId }).ToListAsync())
            .Select(n => (n.Type, n.ReferenceId)).ToList();
    }

    private async Task<HttpResponseMessage> UploadProof(string token, byte[] bytes, string name = "proof.png")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", name);
        return await _guest.PostAsync($"/api/v1/public/requests/{token}/proof", form);
    }

    [Fact]
    public async Task ARequest_HoldsTheSlot_AsAPendingBookingForTheCustomer_AndTellsTheTeam()
    {
        var (owner, venue) = await Venue();
        var token = await Token(venue);

        var row = await Row(token);
        Assert.Equal(("pending", true, Booking.WebSource), (row.Status, row.IsManual, row.Source));
        Assert.Equal(0, row.SystemFee);
        Assert.NotNull(row.CustomerId);
        Assert.NotNull(row.PaymentDeadlineAt);
        Assert.Equal("requested", (await Status(token)).Status);
        Assert.Contains(("web_request", row.Id), await Inbox(owner.Id));

        // The slot is taken for everyone: another guest, and the counter.
        Assert.Equal(HttpStatusCode.Conflict, (await Request(venue, phone: "0797777777")).StatusCode);
        var counter = await _fx.CreateClientFor(owner.Id, "venue_owner").PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date = Date(5), startTime = "18:00", duration = 60,
            paymentMethod = "cliq", isManual = true, customerPhone = "0791111111", customerName = "Walk In",
        });
        Assert.Equal(HttpStatusCode.Conflict, counter.StatusCode);
    }

    [Fact]
    public async Task AcceptingARequest_ConfirmsItWithoutRecordingMoney()
    {
        var (owner, venue) = await Venue();
        var token = await Token(venue);
        var id = (await Row(token)).Id;

        var res = await _fx.CreateClientFor(owner.Id, "venue_owner").PatchAsync($"/api/v1/bookings/{id}/confirm", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal("confirmed", (await Status(token)).Status);
        Assert.Empty(await _fx.LoadPayments(id));
        Assert.Equal(0, (await Row(token)).AmountPaid);
        // Nobody to tell "your booking is confirmed": the guest has no account.
        Assert.DoesNotContain(await Inbox(owner.Id), n => n.Type == "booking_confirmed");
    }

    [Fact]
    public async Task PayingNow_ShowsTheCliqAlias_ThenAProofHoldsTheSlotUntilTheVenueApproves()
    {
        var (owner, venue) = await Venue();
        var token = await Token(venue, payNow: true);

        var waiting = await Status(token);
        Assert.Equal("awaiting_payment", waiting.Status);
        Assert.Equal("throwaway@cliq", waiting.CliqAlias);
        Assert.Equal(4, waiting.Deposit, 3);   // 20% of 20 JOD

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadProof(token, "not an image"u8.ToArray(), "x.png")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await UploadProof(token, Png)).StatusCode);

        var review = await Status(token);
        Assert.Equal("awaiting_review", review.Status);
        Assert.Null(review.CliqAlias);
        var row = await Row(token);
        Assert.Null(row.PaymentDeadlineAt);
        Assert.True(ProofUpload.IsUploadReference(row.PaymentProof!));
        Assert.Contains(("proof_received", row.Id), await Inbox(owner.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await Request(venue, phone: "0797777777")).StatusCode);

        var approve = await _fx.CreateClientFor(owner.Id, "venue_owner")
            .PatchAsJsonAsync($"/api/v1/bookings/{row.Id}/review-proof", new { approved = true });
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        Assert.Equal("confirmed", (await Status(token)).Status);
        Assert.Equal(4, (await Row(token)).AmountPaid, 3);
        Assert.DoesNotContain(await Inbox(owner.Id), n => n.Type == "proof_approved");
    }

    [Fact]
    public async Task ARejectedProof_GivesTheGuestAFreshWindowAndTheReason()
    {
        var (owner, venue) = await Venue();
        var token = await Token(venue, payNow: true);
        Assert.Equal(HttpStatusCode.OK, (await UploadProof(token, Png)).StatusCode);
        var id = (await Row(token)).Id;

        var reject = await _fx.CreateClientFor(owner.Id, "venue_owner")
            .PatchAsJsonAsync($"/api/v1/bookings/{id}/review-proof", new { approved = false, note = "Wrong amount" });
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        var status = await Status(token);
        Assert.Equal("awaiting_payment", status.Status);
        Assert.Equal("Wrong amount", status.ProofNote);
        Assert.NotNull((await Row(token)).PaymentDeadlineAt);
    }

    [Fact]
    public async Task ARequestWithNoAnswer_IsReleasedByTheExpiryJob()
    {
        var (_, venue) = await Venue();
        var token = await Token(venue, time: "20:00");
        var deadline = (await Row(token)).PaymentDeadlineAt!.Value;

        using (var scope = _fx.Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UnpaidBookingSweep>().SweepAsync(deadline.AddMinutes(1), 200);

        Assert.Equal("expired", (await Status(token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Request(venue, time: "20:00", phone: "0797777777")).StatusCode);
    }

    [Fact]
    public async Task TheGuestCanCancel_AndTheTeamHears()
    {
        var (owner, venue) = await Venue();
        var token = await Token(venue, time: "21:00");

        Assert.Equal(HttpStatusCode.OK, (await _guest.PostAsync($"/api/v1/public/requests/{token}/cancel", null)).StatusCode);
        Assert.Equal("cancelled", (await Status(token)).Status);
        Assert.Contains(await Inbox(owner.Id), n => n.Type == "booking_cancelled");
        Assert.Equal(HttpStatusCode.BadRequest, (await _guest.PostAsync($"/api/v1/public/requests/{token}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task ARequestCanBecomePayNow()
    {
        var (_, venue) = await Venue();
        var token = await Token(venue, time: "22:00");
        Assert.True((await Status(token)).CanPayNow);

        Assert.Equal(HttpStatusCode.OK, (await _guest.PostAsync($"/api/v1/public/requests/{token}/pay", null)).StatusCode);
        Assert.Equal("awaiting_payment", (await Status(token)).Status);
    }

    [Fact]
    public async Task ASuspendedCompanyOrInactiveVenue_TakesNoWebBookings()
    {
        var (owner, venue) = await Venue();
        await _fx.Insert(new Company { OwnerId = owner.Id, Name = "Suspended Co", SuspendedAt = DateTime.UtcNow });

        var page = (await (await _guest.GetAsync($"/api/v1/public/v/{venue.Id}")).Content.ReadFromJsonAsync<ApiResponse<WebVenueResponse>>())!.Data!;
        Assert.False(page.AcceptingBookings);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(venue)).StatusCode);

        var (_, inactive) = await Venue(v => v.Status = "inactive");
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(inactive)).StatusCode);
    }

    [Fact]
    public async Task BadInput_IsRefused()
    {
        var (_, venue) = await Venue(v => v.CliqAlias = null);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(venue, phone: "12345")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(venue, website: "spam")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(venue, payNow: true)).StatusCode);   // no CliQ alias
        Assert.Equal(HttpStatusCode.NotFound, (await _guest.GetAsync("/api/v1/public/requests/not-a-token")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _guest.GetAsync("/api/v1/public/v/no-such-venue")).StatusCode);
    }

    [Fact]
    public async Task OneGuest_CannotHoldMoreThanThreeOpenBookings()
    {
        var (_, venue) = await Venue();
        foreach (var time in new[] { "10:00", "11:00", "12:00" })
            await Token(venue, time: time, phone: "0795555555");
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(venue, time: "13:00", phone: "0795555555")).StatusCode);
    }

    [Fact]
    public async Task AConfirmedWebBooking_GetsNoGameReminderAddressedToTheOwner()
    {
        var (owner, venue) = await Venue();
        var token = await Token(venue, time: "16:00");
        var row = await Row(token);
        await _fx.CreateClientFor(owner.Id, "venue_owner").PatchAsync($"/api/v1/bookings/{row.Id}/confirm", null);

        // Pretend it was booked long ago, then run the reminders shortly before kick-off.
        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Bookings.Where(b => b.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(b => b.CreatedAt, DateTime.UtcNow.AddDays(-3)));
            var start = PaymentDeadline.SlotStartUtc(row.Date, row.StartTime)!.Value;
            await scope.ServiceProvider.GetRequiredService<BookingReminders>().RunAsync(start.AddMinutes(-90), new ReminderOptions(), true);
        }
        Assert.DoesNotContain(await Inbox(owner.Id), n => n.Type == "game_reminder");
    }

    [Fact]
    public async Task AVenueSlug_IsValidatedAndUnique()
    {
        var (owner, venue) = await Venue();
        var (_, other) = await Venue();
        var client = _fx.CreateClientFor(owner.Id, "venue_owner");

        var slug = "arena-" + Guid.NewGuid().ToString("N")[..6];
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new { slug = slug.ToUpperInvariant() })).StatusCode);
        Assert.Equal(slug, (await _fx.LoadVenue(venue.Id))!.Slug);
        Assert.Equal(HttpStatusCode.OK, (await _guest.GetAsync($"/api/v1/public/v/{slug}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PatchAsJsonAsync($"/api/v1/venues/{venue.Id}", new { slug = "ملعب" })).StatusCode);
        var otherOwner = _fx.CreateClientFor(other.OwnerId, "venue_owner");
        Assert.Equal(HttpStatusCode.Conflict, (await otherOwner.PatchAsJsonAsync($"/api/v1/venues/{other.Id}", new { slug })).StatusCode);
    }
}
