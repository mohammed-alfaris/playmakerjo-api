using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// What the public (the app, the website, anyone booking) may see and book: an active venue
/// whose company PlayMaker has not suspended. One definition, so a suspended company's venues
/// leave every list, search and booking path together.
/// </summary>
public static class VenueVisibility
{
    public static IQueryable<Venue> OpenToPublic(this IQueryable<Venue> q, AppDbContext db) =>
        q.Where(v => v.Status == "active" && !db.Companies.Any(c => c.OwnerId == v.OwnerId && c.SuspendedAt != null));
}
