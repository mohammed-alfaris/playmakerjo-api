using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Constants;
using SportsVenueApi.Services;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.RateLimiting;

/// <summary>
/// Factory with permit limits of 1 so the second rapid request trips the limiter.
/// Extends TestWebApplicationFactory to inherit the Firebase and connection guards.
/// </summary>
public class LowRateLimitFactory : TestWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimiting:Uploads:PermitLimit", "1");
        builder.UseSetting("RateLimiting:BookingCreate:PermitLimit", "1");
    }
}

[Collection("Api")]
public class RateLimitTests : IDisposable
{
    private readonly DatabaseFixture _fx;
    private readonly LowRateLimitFactory _factory;

    public RateLimitTests(DatabaseFixture fx)
    {
        _fx = fx;
        _factory = new LowRateLimitFactory();
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient ClientFor(string userId, string role)
    {
        var jwt = _factory.Services.GetRequiredService<JwtService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", jwt.CreateAccessToken(userId, role));
        return client;
    }

    [Fact]
    public async Task BookingCreate_SecondRapidRequest_Returns429()
    {
        var venue = await _fx.CreateBasketballVenue(_fx.OwnerAId);
        var player = await _fx.CreatePlayer();
        var client = ClientFor(player.Id, "player");
        var date = PlatformConstants.JordanToday().AddDays(7).ToString("yyyy-MM-dd");

        var first = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date,
            startTime = "10:00", duration = 60, paymentMethod = "cliq"
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/v1/bookings", new
        {
            venueId = venue.Id, sport = "basketball", date,
            startTime = "12:00", duration = 60, paymentMethod = "cliq"
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task Uploads_SecondRapidRequest_Returns429()
    {
        var player = await _fx.CreatePlayer();
        var client = ClientFor(player.Id, "player");

        var first = await client.PostAsync("/api/v1/uploads", UploadForm());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await client.PostAsync("/api/v1/uploads", UploadForm());
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    private static MultipartFormDataContent UploadForm()
    {
        var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return new MultipartFormDataContent
        {
            { file, "file", "test.png" },
            { new StringContent("avatar"), "category" }
        };
    }
}
