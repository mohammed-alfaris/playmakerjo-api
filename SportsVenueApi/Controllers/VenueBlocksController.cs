using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

/// <summary>
/// Blocked time: maintenance, holidays, private events. See <see cref="VenueBlock"/>.
///
/// Routes:
///   GET    /api/v1/venues/{venueId}/blocks?from=&amp;to=   — blocks touching [from, to]
///   POST   /api/v1/venues/{venueId}/blocks                — block time; lists bookings already inside it
///   DELETE /api/v1/venues/{venueId}/blocks/{blockId}      — reopen it
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/venues/{venueId}/blocks")]
public class VenueBlocksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AccessContext _access;

    public VenueBlocksController(AppDbContext db, AccessContext access)
    {
        _db = db;
        _access = access;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";

    private static readonly string[] TimeFormats = ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"];

    private static bool TryLocal(string? s, out DateTime value) =>
        DateTime.TryParseExact(s, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    private static string Local(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    private static VenueBlockResponse ToDto(VenueBlock b) => new()
    {
        Id = b.Id,
        VenueId = b.VenueId,
        PitchId = b.PitchId,
        StartsAt = Local(b.StartsAt),
        EndsAt = Local(b.EndsAt),
        Reason = b.Reason,
        CreatedAt = b.CreatedAt,
    };

    [HttpGet]
    public async Task<IActionResult> List(string venueId, [FromQuery] string? from = null, [FromQuery] string? to = null)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });
        if (!_access.Can(StaffPermissions.BookingsView, venue))
            return Forbid();

        // Default: from today on. Dates are whole Amman days, inclusive.
        var fromDate = DateTime.TryParse(from, out var f) ? f.Date : PlatformConstants.JordanToday();
        var toExclusive = DateTime.TryParse(to, out var t) ? t.Date.AddDays(1) : fromDate.AddDays(366);

        var blocks = await _db.VenueBlocks.AsNoTracking()
            .Where(b => b.VenueId == venueId && b.StartsAt < toExclusive && b.EndsAt > fromDate)
            .OrderBy(b => b.StartsAt)
            .ToListAsync();

        return Ok(new ApiResponse<List<VenueBlockResponse>> { Data = blocks.Select(ToDto).ToList() });
    }

    [HttpPost]
    public async Task<IActionResult> Create(string venueId, [FromBody] CreateVenueBlockRequest req)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });
        if (!_access.Can(StaffPermissions.BookingsManage, venue))
            return Forbid();

        if (!TryLocal(req.StartsAt, out var startsAt) || !TryLocal(req.EndsAt, out var endsAt))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Use yyyy-MM-ddTHH:mm for the start and end" });
        if (endsAt <= startsAt)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "The block must end after it starts" });
        if (endsAt - startsAt > TimeSpan.FromDays(92))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "A block can be at most three months long" });
        if (endsAt.Date < PlatformConstants.JordanToday())
            return BadRequest(new ApiResponse<object> { Success = false, Message = "That time has already passed" });

        var pitches = PitchSizes.ResolvedPitches(venue);
        var pitchId = string.IsNullOrEmpty(req.PitchId) ? null : req.PitchId;
        if (pitchId != null && pitches.All(p => p.Id != pitchId))
            return NotFound(new ApiResponse<object> { Success = false, Message = "Pitch not found" });

        var block = new VenueBlock
        {
            VenueId = venueId,
            PitchId = pitchId,
            StartsAt = startsAt,
            EndsAt = endsAt,
            Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
            CreatedByUserId = UserId,
        };
        _db.VenueBlocks.Add(block);
        await _db.SaveChangesAsync();

        // The block does not cancel anything. Bookings already inside it are listed so the
        // desk can move or cancel them with the customer — a silent cancellation would leave
        // someone turning up to a locked gate, and a refusal would leave the pitch sellable.
        var candidates = await _db.Bookings.AsNoTracking()
            .Include(b => b.Customer).Include(b => b.Player)
            .Where(b => b.VenueId == venueId
                && b.Status != "cancelled" && b.Status != "completed" && b.Status != "no_show"
                && b.Date >= startsAt.Date.AddDays(-1) && b.Date <= endsAt.Date
                && b.StartTime != null)
            .AsSplitQuery()
            .ToListAsync();
        var overlapping = candidates
            .Where(b => TimeSpan.TryParse(b.StartTime, out var s)
                && VenueBlocks.FirstOverlap([block], PitchOf(b, venue, pitches), b.Date, s, b.Duration) != null)
            .OrderBy(b => b.Date).ThenBy(b => b.StartTime)
            .Select(b => new BlockedBookingInfo
            {
                Id = b.Id,
                Date = b.Date.ToString("yyyy-MM-dd"),
                StartTime = b.StartTime,
                Duration = b.Duration,
                PitchId = b.PitchId,
                CustomerName = b.Customer?.Name ?? (b.IsManual ? null : b.Player?.Name),
                Status = b.Status,
            })
            .ToList();

        return Ok(new ApiResponse<CreateVenueBlockResponse>
        {
            Data = new CreateVenueBlockResponse { Block = ToDto(block), OverlappingBookings = overlapping },
            Message = overlapping.Count == 0
                ? "Time blocked"
                : $"Time blocked; {overlapping.Count} booking(s) are already inside it"
        });
    }

    [HttpDelete("{blockId}")]
    public async Task<IActionResult> Delete(string venueId, string blockId)
    {
        var block = await _db.VenueBlocks.Include(b => b.Venue).FirstOrDefaultAsync(b => b.Id == blockId && b.VenueId == venueId);
        if (block == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Block not found" });
        if (!_access.Can(StaffPermissions.BookingsManage, block.Venue))
            return Forbid();

        _db.VenueBlocks.Remove(block);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Data = null, Message = "Block removed" });
    }

    /// <summary>The pitch a booking lives on; a legacy row with none is on the first pitch of its sport.</summary>
    private static string? PitchOf(Booking b, Venue v, List<PitchDto> pitches) =>
        b.PitchId ?? pitches.FirstOrDefault(p => string.Equals(p.Sport, b.Sport, StringComparison.OrdinalIgnoreCase))?.Id;
}
