using System.Net;
using System.Net.Http.Json;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Staff;
using SportsVenueApi.DTOs.Users;
using SportsVenueApi.Tests.Infrastructure;

namespace SportsVenueApi.Tests.Money;

internal static class MoneyTestKit
{
    /// <summary>
    /// A clerk hired by the owner behind <paramref name="ownerClient"/>, on a custom role with
    /// exactly these permissions — made through the API, the way an owner does it.
    /// </summary>
    public static async Task<HttpClient> ClerkWith(this DatabaseFixture fx, HttpClient ownerClient, params string[] permissions)
    {
        var role = await ownerClient.PostAsJsonAsync("/api/v1/staff-roles",
            new { name = "Role " + Guid.NewGuid().ToString("N")[..6], permissions });
        var roleId = (await role.Content.ReadFromJsonAsync<ApiResponse<StaffRoleResponse>>())!.Data!.Id;
        var hire = await ownerClient.PostAsJsonAsync("/api/v1/users", new
        {
            name = "Clerk", email = $"clerk-{Guid.NewGuid():N}@test.local", password = DatabaseFixture.TestPassword,
            role = "venue_staff", staffRoleId = roleId,
        });
        Assert.Equal(HttpStatusCode.OK, hire.StatusCode);
        var clerkId = (await hire.Content.ReadFromJsonAsync<ApiResponse<UserResponse>>())!.Data!.Id;
        return await fx.CreateClientForUserAsync(clerkId);
    }
}
