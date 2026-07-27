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

    /// <summary>
    /// Staff-aware READ access: may this caller see this venue's schedule and bookings?
    ///
    /// <paramref name="staffOwnerId"/> comes from the token's <c>owner_id</c> claim. It is
    /// null for every non-staff role, and null for staff rows created before staff→owner
    /// linking existed — both of which must resolve to NO access. Writing this as an
    /// allow-list rather than a deny-list is what keeps an unlinked staff account, or any
    /// future role, out by default.
    /// </summary>
    public static bool CanView(Venue venue, string userId, string userRole, string? staffOwnerId) =>
        CanManage(venue, userId, userRole)
        || (userRole == "venue_staff"
            && !string.IsNullOrEmpty(staffOwnerId)
            && venue.OwnerId == staffOwnerId);

    /// <summary>
    /// Staff-aware WRITE access: may this caller create or change bookings on this venue?
    /// Same rule as <see cref="CanView"/> plus the existing <c>User.Permissions</c> gate —
    /// a "read" staff member can watch the schedule but never move money or slots.
    /// </summary>
    public static bool CanWrite(
        Venue venue, string userId, string userRole, string? staffOwnerId, string? staffPermissions) =>
        CanManage(venue, userId, userRole)
        || (userRole == "venue_staff"
            && staffPermissions == "write"
            && !string.IsNullOrEmpty(staffOwnerId)
            && venue.OwnerId == staffOwnerId);
}
