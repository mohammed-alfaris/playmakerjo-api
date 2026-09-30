using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Bookings;

/// <summary>
/// How the app actually sends a payment proof: it uploads the screenshot through POST /uploads,
/// then PATCHes upload-proof with the URL it got back.
///
/// Every other proof test sends inline base64, a shape the app has never used — which is how
/// upload-proof came to validate every proof as base64, reject every URL the app sent, and still
/// pass the whole suite. With it, no CliQ payment could complete. This file exercises the real
/// sequence end to end, and pins what a URL may point at.
/// </summary>
[Collection("Api")]
public class ProofUploadUrlTests
{
    private readonly DatabaseFixture _fx;

    public ProofUploadUrlTests(DatabaseFixture fx) => _fx = fx;

    private static string FutureDate => PlatformConstants.JordanToday().AddDays(17).ToString("yyyy-MM-dd");

    /// <summary>
    /// A fresh player per test: /uploads is rate-limited per user, and a shared player uploading
    /// from several tests would trip the limiter depending on test order.
    /// </summary>
    private async Task<(HttpClient Client, string BookingId)> PlayerWithPendingCliqBooking()
    {
        var player = await _fx.CreatePlayer();
        var client = _fx.CreateClientFor(player.Id, "player");
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);

        var res = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id,
            sport = "basketball",
            date = FutureDate,
            startTime = "10:00",
            duration = 60,
            paymentMethod = "cliq",
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var booking = (await res.Content.ReadFromJsonAsync<ApiResponse<BookingResponse>>())!.Data!;
        Assert.Equal("pending_payment", booking.Status);
        return (client, booking.Id);
    }

    /// <summary>Uploads a real PNG exactly as the app does and returns the URL the API hands back.</summary>
    private static async Task<string> Upload(HttpClient client, string category)
    {
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var form = new MultipartFormDataContent
        {
            { file, "file", "screenshot.png" },
            { new StringContent(category), "category" },
        };

        var res = await client.PostAsync("/api/v1/uploads", form);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").GetProperty("url").GetString()!;
    }

    private static Task<HttpResponseMessage> SendProof(HttpClient client, string bookingId, string proof) =>
        client.PatchAsJsonAsync($"/api/v1/bookings/{bookingId}/upload-proof", new { paymentProof = proof });

    // -------------------------------------------------------------------- the app's flow

    [Fact]
    public async Task TheUrlFromTheUploadEndpoint_IsAccepted()
    {
        // Exactly what the app does. With Uploads:BaseUrl empty, as appsettings ships it, the
        // upload endpoint hands back a root-relative "/uploads/proofs/..." path.
        var (client, bookingId) = await PlayerWithPendingCliqBooking();
        var url = await Upload(client, "proof");
        Assert.StartsWith("/uploads/proofs/", url);

        var res = await SendProof(client, bookingId, url);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = await _fx.LoadBooking(bookingId);
        Assert.Equal(url, row!.PaymentProof);
        Assert.Equal("pending_review", row.Status);
        Assert.Equal("pending_review", row.PaymentProofStatus);
    }

    [Fact]
    public async Task TheAbsoluteUrlProductionHandsOut_IsAccepted()
    {
        // Production sets Uploads:BaseUrl, so the app receives an absolute URL for the same file.
        var (client, bookingId) = await PlayerWithPendingCliqBooking();
        var url = "https://api.playmakerjo.com" + await Upload(client, "proof");

        var res = await SendProof(client, bookingId, url);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("pending_review", (await _fx.LoadBooking(bookingId))!.Status);
    }

    [Fact]
    public async Task InlineBase64JpegStartingWithASlash_IsStillReadAsImageData()
    {
        // Base64 JPEG data begins "/9j/". Routing "anything starting with /" to the URL check
        // would have refused every inline JPEG; only a "/uploads/" prefix means a reference.
        var (client, bookingId) = await PlayerWithPendingCliqBooking();
        var jpeg = Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01]);
        Assert.StartsWith("/9j/", jpeg);

        var res = await SendProof(client, bookingId, jpeg);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    // -------------------------------------------------------------------- what a URL may not be

    [Fact]
    public async Task AnUploadedImageFromAnotherFolder_IsNotAProof()
    {
        // A real file we stored — but an avatar. Only the proofs folder counts.
        var (client, bookingId) = await PlayerWithPendingCliqBooking();
        var avatarUrl = await Upload(client, "avatar");

        var res = await SendProof(client, bookingId, avatarUrl);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Null((await _fx.LoadBooking(bookingId))!.PaymentProof);
    }

    [Theory]
    // Well-shaped, but no such file was ever uploaded.
    [InlineData("https://api.playmakerjo.com/uploads/proofs/00000000-0000-0000-0000-000000000000.jpg")]
    // Somebody else's image entirely.
    [InlineData("https://example.com/cat.jpg")]
    // Climbing out of the proofs folder.
    [InlineData("https://api.playmakerjo.com/uploads/proofs/../../appsettings.json")]
    [InlineData("https://api.playmakerjo.com/uploads/proofs/%2e%2e/%2e%2e/appsettings.json")]
    // Extra baggage the stored value would carry around.
    [InlineData("https://api.playmakerjo.com/uploads/proofs/00000000-0000-0000-0000-000000000000.jpg?x=1")]
    // Not http(s).
    [InlineData("ftp://api.playmakerjo.com/uploads/proofs/00000000-0000-0000-0000-000000000000.jpg")]
    // The relative forms of the same mistakes.
    [InlineData("/uploads/proofs/00000000-0000-0000-0000-000000000000.jpg")]
    [InlineData("/uploads/proofs/../../appsettings.json")]
    [InlineData("/uploads/avatars/00000000-0000-0000-0000-000000000000.jpg")]
    public async Task AUrlThatIsNotOneOfOurStoredProofs_IsRefused(string url)
    {
        var (client, bookingId) = await PlayerWithPendingCliqBooking();

        var res = await SendProof(client, bookingId, url);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var row = await _fx.LoadBooking(bookingId);
        Assert.Null(row!.PaymentProof);
        Assert.Equal("pending_payment", row.Status);
    }
}
