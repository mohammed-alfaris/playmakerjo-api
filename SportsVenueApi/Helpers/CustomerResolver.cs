using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.Models;

namespace SportsVenueApi.Helpers;

/// <summary>
/// Find-or-create the venue's customer, keyed on (owner, normalised phone).
///
/// Lifted out of BookingsController so standing weekly reservations can use the same path.
/// Copying it would have been the third time a rule in this codebase was duplicated and
/// then drifted — attendance and the "lapsed" rule both did exactly that.
/// </summary>
public static class CustomerResolver
{
    /// <summary>
    /// Returns null — meaning "no customer recorded" — when no usable phone was given. That
    /// is deliberate: the phone IS the identity, and a nameless row with no number would be
    /// an un-deduplicable ghost polluting the book forever. A booking without a phone is
    /// still a perfectly good booking, and refusing one over a phone number would teach the
    /// counter to type 0000000000.
    ///
    /// A stored name is never overwritten by a later booking: the owner may have corrected
    /// the spelling in the customer sheet, and a hurried re-type at the counter must not
    /// undo that. An empty stored name is filled in when one finally arrives.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        AppDbContext db, string ownerId, string? rawPhone, string? rawName, string createdByUserId)
    {
        var phone = PhoneNormalizer.ToE164Jo(rawPhone);
        if (phone == null) return null;

        var name = rawName?.Trim() ?? "";
        if (name.Length > 120) name = name[..120];

        var existing = await db.Customers
            .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.Phone == phone);

        if (existing != null)
        {
            if (string.IsNullOrWhiteSpace(existing.Name) && name.Length > 0)
            {
                existing.Name = name;
                existing.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            return existing.Id;
        }

        var customer = new Customer
        {
            OwnerId = ownerId,
            Phone = phone,
            Name = name,
            CreatedByUserId = createdByUserId,
        };
        db.Customers.Add(customer);

        try
        {
            await db.SaveChangesAsync();
            return customer.Id;
        }
        catch (DbUpdateException)
        {
            // Two clerks taking the same person's booking at the same moment both miss the
            // read and both insert; the unique index rejects one. Re-read rather than fail
            // the booking — the customer exists either way, which is all we needed.
            db.Entry(customer).State = EntityState.Detached;
            var raced = await db.Customers
                .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.Phone == phone);
            return raced?.Id;
        }
    }
}
