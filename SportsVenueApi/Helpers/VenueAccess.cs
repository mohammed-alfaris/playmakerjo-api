using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// Central ownership check for venue management: super_admin may manage any
/// venue, venue_owner only venues they own. Everyone else (players, staff) may not.
/// </summary>
public static class VenueAccess
{
    public static bool CanManage(Venue venue, string userId, string userRole) =>
        userRole == "super_admin" || (userRole == "venue_owner" && venue.OwnerId == userId);
}
