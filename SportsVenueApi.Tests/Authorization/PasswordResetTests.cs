using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Services;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Authorization;

/// <summary>
/// Admin-initiated password reset — the whole of password recovery in this system, since
/// there is no email infrastructure and no self-service forgot-password flow. Before it
/// existed, a venue owner who forgot their password could only be helped by hand-editing the
/// database.
///
/// The load-bearing test here is <see cref="ARefreshTokenIssuedBeforeTheResetIsRejected"/>.
/// A reset that only blocks future logins is theatre: refresh tokens live seven days and
/// /auth/refresh validated signature, expiry and Status only — it never looked at the
/// password. So whoever already held a refresh cookie kept minting access tokens for a week,
/// which is exactly the person a reset is aimed at. Everything else in this file is scaffolding
/// around that one property.
/// </summary>
[Collection("Api")]
public class PasswordResetTests
{
    private readonly DatabaseFixture _fx;

    public PasswordResetTests(DatabaseFixture fx) => _fx = fx;

    private HttpClient Admin => _fx.CreateClientFor(_fx.AdminId, "super_admin");
    private HttpClient OwnerA => _fx.CreateClientFor(_fx.OwnerAId, "venue_owner");

    private static Task<HttpResponseMessage> Reset(HttpClient client, string userId) =>
        client.PostAsync($"/api/v1/users/{userId}/reset-password", null);

    private static async Task<ResetPasswordResponse> ResetOk(HttpClient client, string userId)
    {
        var res = await Reset(client, userId);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<ApiResponse<ResetPasswordResponse>>())!.Data!;
    }

    // ── Authorization ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAdminCanResetAnyone()
    {
        // A throwaway owner, never a seeded one. The seeded rows are shared by the whole
        // suite and PayerAccessTests signs in as them with TestPassword — resetting one here
        // breaks a file with no visible connection to this one, which is exactly what
        // happened the first time this test was written.
        var owner = await _fx.CreateOwner();

        var data = await ResetOk(Admin, owner.Id);

        Assert.Equal(owner.Email, data.Email);
        Assert.NotEmpty(data.TemporaryPassword);
    }

    [Fact]
    public async Task AnOwnerCanResetTheirOwnClerk()
    {
        var clerk = await _fx.CreateStaff(_fx.OwnerAId, "write");

        var data = await ResetOk(OwnerA, clerk.Id);

        Assert.Equal(clerk.Email, data.Email);
    }

    [Fact]
    public async Task AnOwnerCannotResetAnotherOwnersClerk()
    {
        var clerk = await _fx.CreateStaff(_fx.OwnerBId, "write");

        var res = await Reset(OwnerA, clerk.Id);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AnOwnerCannotResetAnotherOwner()
    {
        // The employer rule keys on venue_staff. An owner is nobody's staff, so this must not
        // slip through on the ManagedByOwnerId comparison alone.
        var otherOwner = await _fx.CreateOwner();

        var res = await Reset(OwnerA, otherOwner.Id);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AClerkCannotResetAnybody()
    {
        var clerk = await _fx.CreateClientForUserAsync(_fx.StaffAWriteId);

        var res = await Reset(clerk, _fx.OwnerAId);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ── What the reset actually does ────────────────────────────────────────────────────

    [Fact]
    public async Task TheStoredHashChangesAndTheNewPasswordVerifies()
    {
        var clerk = await _fx.CreateStaff(_fx.OwnerAId);
        var before = (await _fx.LoadUser(clerk.Id))!.PasswordHash;

        var data = await ResetOk(Admin, clerk.Id);

        var after = (await _fx.LoadUser(clerk.Id))!;
        Assert.NotEqual(before, after.PasswordHash);
        // The plaintext is returned once and never stored; only the hash proves they match.
        Assert.True(BCrypt.Net.BCrypt.Verify(data.TemporaryPassword, after.PasswordHash));
        Assert.NotNull(after.PasswordChangedAt);
    }

    [Fact]
    public async Task TheOldPasswordNoLongerVerifies()
    {
        // Throwaway users hash DatabaseFixture.TestPassword too, so this is a real "the
        // password you had stopped working" assertion rather than a tautology.
        var owner = await _fx.CreateOwner();
        Assert.True(BCrypt.Net.BCrypt.Verify(DatabaseFixture.TestPassword, owner.PasswordHash));

        await ResetOk(Admin, owner.Id);

        var after = (await _fx.LoadUser(owner.Id))!;
        Assert.False(BCrypt.Net.BCrypt.Verify(DatabaseFixture.TestPassword, after.PasswordHash));
    }

    [Fact]
    public async Task TwoResetsNeverProduceTheSamePassword()
    {
        var clerk = await _fx.CreateStaff(_fx.OwnerAId);

        var first = await ResetOk(Admin, clerk.Id);
        var second = await ResetOk(Admin, clerk.Id);

        Assert.NotEqual(first.TemporaryPassword, second.TemporaryPassword);
        Assert.True(first.TemporaryPassword.Length >= 8);
    }

    // ── Session revocation — the reason this feature is worth anything ──────────────────

    /// <summary>Sends a refresh request carrying the given token as the refresh cookie.</summary>
    private async Task<HttpResponseMessage> RefreshWith(string refreshToken)
    {
        var client = _fx.Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        req.Headers.Add("Cookie", $"refresh_token={refreshToken}");
        return await client.SendAsync(req);
    }

    private string RefreshTokenFor(SportsVenueApi.Models.User user)
    {
        var jwt = _fx.Factory.Services.GetRequiredService<JwtService>();
        return jwt.CreateRefreshToken(user.Id, user.Role, user.ManagedByOwnerId, user.Permissions);
    }

    [Fact]
    public async Task ARefreshTokenIssuedBeforeTheResetIsRejected()
    {
        // THE test. Everything else only stops someone signing in again; this is what closes
        // the seven-day window on a session that is already open.
        var clerk = await _fx.CreateStaff(_fx.OwnerAId, "write");
        var token = RefreshTokenFor((await _fx.LoadUser(clerk.Id))!);

        // Works beforehand — otherwise the assertion after the reset proves nothing.
        Assert.Equal(HttpStatusCode.OK, (await RefreshWith(token)).StatusCode);

        // iat has one-second resolution, so without this the reset can land inside the same
        // second the token was minted and be correctly allowed through.
        await Task.Delay(1100);
        await ResetOk(Admin, clerk.Id);

        var after = await RefreshWith(token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ARefreshTokenIssuedAfterTheResetStillWorks()
    {
        // The other half: revocation must not be a one-way door that locks the user out even
        // once they have signed in with the new password.
        var clerk = await _fx.CreateStaff(_fx.OwnerAId, "write");
        await ResetOk(Admin, clerk.Id);

        await Task.Delay(1100);
        var fresh = RefreshTokenFor((await _fx.LoadUser(clerk.Id))!);

        Assert.Equal(HttpStatusCode.OK, (await RefreshWith(fresh)).StatusCode);
    }

    [Fact]
    public async Task AnUntouchedAccountIsUnaffected()
    {
        // password_changed_at is null for every pre-existing row, and null must mean "never
        // reset", not "invalidate everything issued before the migration ran".
        var untouched = await _fx.CreatePlayer();
        Assert.Null(untouched.PasswordChangedAt);

        var token = RefreshTokenFor(untouched);

        Assert.Equal(HttpStatusCode.OK, (await RefreshWith(token)).StatusCode);
    }
}
