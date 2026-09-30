using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.RateLimiting;

/// <summary>
/// Stands in for the network: sets the connection address from a test-only header before the
/// app's own pipeline runs, so a test can say "this arrived from nginx on the Docker gateway"
/// or "this arrived straight from the internet". TestServer otherwise has no address at all.
/// </summary>
internal sealed class SimulatedNetworkStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Connection-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var ip))
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
            await nextMiddleware();
        });
        next(app);
    };
}

public class BehindProxyFactory : LowRateLimitFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, SimulatedNetworkStartupFilter>());
    }
}

/// <summary>
/// In production every request reaches the API through nginx, so the connection address is
/// always the Docker gateway. The login limiter used that address as its key, which made it one
/// shared budget for the whole platform. These tests pin the fix: the real client address from
/// X-Forwarded-For is the key — but only when the request really came through a proxy.
/// </summary>
[Collection("Api")]
public class ForwardedClientTests : IDisposable
{
    private readonly BehindProxyFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private const string DockerGateway = "172.18.0.1";
    private static readonly object BadLogin = new { email = "nobody@test.local", password = "wrong-on-purpose" };

    private async Task<HttpStatusCode> Login(string connectionIp, string? forwardedFor)
    {
        var client = _factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(BadLogin) };
        req.Headers.Add(SimulatedNetworkStartupFilter.Header, connectionIp);
        if (forwardedFor != null) req.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task TwoPeopleBehindTheProxy_EachGetTheirOwnLoginBudget()
    {
        // The limit is 1 per minute in this factory: a second attempt from the same person trips it.
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(DockerGateway, "203.0.113.10"));
        Assert.Equal((HttpStatusCode)429, await Login(DockerGateway, "203.0.113.10"));

        // Someone else, same proxy. Before the fix this was a 429 too: one bucket for everyone.
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(DockerGateway, "198.51.100.20"));
    }

    [Fact]
    public async Task OnlyTheHopNginxAddedCounts_SoAClientCannotPickItsOwnBucket()
    {
        // nginx appends the real address to whatever the client sent; the last entry is the one
        // to trust. A client rotating a fake first entry is still the same client.
        Assert.Equal(HttpStatusCode.Unauthorized, await Login(DockerGateway, "1.1.1.1, 203.0.113.30"));
        Assert.Equal((HttpStatusCode)429, await Login(DockerGateway, "2.2.2.2, 203.0.113.30"));
    }

    [Fact]
    public async Task AForwardedHeaderFromOutsideTheHost_IsIgnored()
    {
        // A connection from a public address is not our proxy, so its header is not believed.
        Assert.Equal(HttpStatusCode.Unauthorized, await Login("8.8.8.8", "203.0.113.40"));
        Assert.Equal((HttpStatusCode)429, await Login("8.8.8.8", "203.0.113.41"));
    }
}
