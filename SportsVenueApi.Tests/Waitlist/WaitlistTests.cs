using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.Data;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Waitlist;

/// <summary>
/// The website's two sign-up forms. The suite's database is built from migrations, so these
/// passing is also the proof that the waitlist tables now exist on a fresh database — before
/// the migration regained its designer file they did not, and every post here was a 500.
/// </summary>
[Collection("Api")]
public class WaitlistTests
{
    private readonly DatabaseFixture _fx;

    public WaitlistTests(DatabaseFixture fx) => _fx = fx;

    private async Task<int> AdminNoticesFor(string venueName)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.CountAsync(n =>
            n.UserId == _fx.AdminId && n.Type == "venue_lead" && n.Body.Contains(venueName));
    }

    [Fact]
    public async Task AVenueOwnerSigningUp_IsSaved_AndTheAdminIsTold()
    {
        var anonymous = _fx.Factory.CreateClient();
        var venueName = "Lead Arena " + Guid.NewGuid().ToString("N")[..6];
        var body = new
        {
            contactName = "Sami", venueName, city = "Amman", phone = "+962790001234",
            email = $"lead-{Guid.NewGuid():N}@test.local", sports = new[] { "football" },
        };

        var first = await anonymous.PostAsJsonAsync("/api/v1/waitlist/venue", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(1, await AdminNoticesFor(venueName));

        // The same email again is acknowledged, not duplicated, and not re-announced.
        var again = await anonymous.PostAsJsonAsync("/api/v1/waitlist/venue", body);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, await AdminNoticesFor(venueName));

        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.VenueWaitlist.CountAsync(v => v.VenueName == venueName));
    }

    [Fact]
    public async Task APlayerSigningUp_IsSaved()
    {
        var res = await _fx.Factory.CreateClient().PostAsJsonAsync("/api/v1/waitlist/player",
            new { email = $"player-{Guid.NewGuid():N}@test.local" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}
