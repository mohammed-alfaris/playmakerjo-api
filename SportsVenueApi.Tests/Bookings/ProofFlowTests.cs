using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Models;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

[Collection("Api")]
public class ProofFlowTests
{
    private const string ProofImage = "data:image/png;base64,iVBORw0KGgo=";

    private readonly DatabaseFixture _fx;

    public ProofFlowTests(DatabaseFixture fx)
    {
        _fx = fx;
    }

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(7).ToString("yyyy-MM-dd");

    private async Task<(Venue Venue, string BookingId)> CreatePendingBooking(string paymentMethod = "cliq")
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");
        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = FutureDate,
            startTime = "10:00",
            duration = 60,
            paymentMethod
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        return (venue, body!.Data!.Id);
    }

    private static Task<HttpResponseMessage> Upload(HttpClient client, string bookingId) =>
        client.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = ProofImage });

    [Fact]
    public async Task Upload_ByNonOwningPlayer_Returns403()
    {
        var (_, bookingId) = await CreatePendingBooking();
        var otherPlayer = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(otherPlayer.Id, "player");

        var res = await Upload(client, bookingId);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var row = await _fx.LoadBooking(bookingId);
        Assert.Null(row!.PaymentProof);
        Assert.Equal("pending_payment", row.Status);
    }

    [Fact]
    public async Task Upload_WhenNotPendingPayment_Returns400()
    {
        var (_, bookingId) = await CreatePendingBooking();
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var first = await Upload(client, bookingId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await Upload(client, bookingId);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task Upload_OnNonCliqBooking_Returns400()
    {
        var (_, bookingId) = await CreatePendingBooking(paymentMethod: "cash");
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await Upload(client, bookingId);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Contains("CliQ", body!.Message);
    }

    [Fact]
    public async Task Upload_Happy_MovesToPendingReview()
    {
        var (_, bookingId) = await CreatePendingBooking();
        var client = _fx.CreateClientFor(_fx.PlayerId, "player");

        var res = await Upload(client, bookingId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        Assert.Equal("pending_review", body!.Data!.Status);
        Assert.Equal("pending_review", body.Data.PaymentProofStatus);

        var row = await _fx.LoadBooking(bookingId);
        Assert.Equal("pending_review", row!.Status);
        Assert.Equal("pending_review", row.PaymentProofStatus);
        Assert.Equal(ProofImage, row.PaymentProof);
    }

    [Fact]
    public async Task Review_ByPlayer_Returns403()
    {
        var (_, bookingId) = await CreatePendingBooking();
        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        await Upload(player, bookingId);

        var res = await player.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/review-proof", new { approved = true });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Review_ByNonOwningOwner_Returns403()
    {
        var (_, bookingId) = await CreatePendingBooking();
        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        await Upload(player, bookingId);

        var otherOwner = _fx.CreateClientFor(_fx.OwnerCId, "venue_owner");
        var res = await otherOwner.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/review-proof", new { approved = true });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        var row = await _fx.LoadBooking(bookingId);
        Assert.Equal("pending_review", row!.Status);
    }

    [Fact]
    public async Task Approve_ConfirmsAndPaysDeposit()
    {
        var (_, bookingId) = await CreatePendingBooking();
        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        await Upload(player, bookingId);

        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await owner.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/review-proof", new { approved = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        Assert.Equal("confirmed", body!.Data!.Status);
        Assert.True(body.Data.DepositPaid);
        Assert.Equal(body.Data.DepositAmount, body.Data.AmountPaid, 3);

        var row = await _fx.LoadBooking(bookingId);
        Assert.Equal("confirmed", row!.Status);
        Assert.Equal("approved", row.PaymentProofStatus);
        Assert.True(row.DepositPaid);
        Assert.Equal(row.DepositAmount, row.AmountPaid, 3);
    }

    [Fact]
    public async Task Reject_WithNote_ResetsToPendingPayment()
    {
        var (_, bookingId) = await CreatePendingBooking();
        var player = _fx.CreateClientFor(_fx.PlayerId, "player");
        await Upload(player, bookingId);

        var owner = _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");
        var res = await owner.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/review-proof",
            new { approved = false, note = "Screenshot is blurry" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>();
        Assert.Equal("pending_payment", body!.Data!.Status);

        var row = await _fx.LoadBooking(bookingId);
        Assert.Equal("pending_payment", row!.Status);
        Assert.Equal("rejected", row.PaymentProofStatus);
        Assert.Equal("Screenshot is blurry", row.PaymentProofNote);
        Assert.Null(row.PaymentProof);
        Assert.False(row.DepositPaid);
    }
}
