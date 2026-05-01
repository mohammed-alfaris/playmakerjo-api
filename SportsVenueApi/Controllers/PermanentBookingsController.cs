using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.PermanentBookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Models;

namespace SportsVenueApi.Controllers;

/// <summary>
/// Owner-managed standing reservations. These are virtual "always booked" rules
/// keyed by (pitch, day-of-week, start-time, duration). They never materialise
/// into <see cref="Booking"/> rows — the availability endpoint and booking
/// conflict checker simply consult the active permanents on every relevant day.
///
/// Routes:
///   GET    /api/v1/venues/{venueId}/permanent-bookings  — list active+cancelled for a venue
///   POST   /api/v1/venues/{venueId}/permanent-bookings  — create a new permanent
///   PATCH  /api/v1/permanent-bookings/{id}/cancel       — soft-cancel (frees the slot)
///   DELETE /api/v1/permanent-bookings/{id}              — hard delete (super_admin only)
/// </summary>
[ApiController]
[Authorize]
public class PermanentBookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<PermanentBookingsController> _logger;

    public PermanentBookingsController(AppDbContext db, ILogger<PermanentBookingsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    // GET /api/v1/venues/{venueId}/permanent-bookings
    [HttpGet("api/v1/venues/{venueId}/permanent-bookings")]
    public async Task<IActionResult> List(string venueId, [FromQuery] string? status = null)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (!CanManageVenue(venue))
            return Forbid();

        var q = _db.PermanentBookings.Where(p => p.VenueId == venueId);
        if (!string.IsNullOrEmpty(status))
            q = q.Where(p => p.Status == status);

        var rows = await q.OrderByDescending(p => p.CreatedAt).ToListAsync();
        return Ok(new ApiResponse<List<PermanentBookingDto>>
        {
            Data = rows.Select(ToDto).ToList()
        });
    }

    // POST /api/v1/venues/{venueId}/permanent-bookings
    [HttpPost("api/v1/venues/{venueId}/permanent-bookings")]
    public async Task<IActionResult> Create(string venueId, [FromBody] CreatePermanentBookingRequest req)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (!CanManageVenue(venue))
            return Forbid();

        // 1. Validate day-of-week.
        if (req.DayOfWeek < 0 || req.DayOfWeek > 6)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "dayOfWeek must be 0..6 (0=Sunday)" });

        // 2. Validate start-time + duration.
        if (string.IsNullOrEmpty(req.StartTime) || !TimeSpan.TryParse(req.StartTime, out var startTimeSpan))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid startTime format. Use HH:mm" });
        if (req.Duration <= 0)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Duration must be positive" });

        // 3. Resolve target pitch. If pitchId is given, it must belong to this venue.
        //    If omitted, fall back to the single resolved pitch (legacy single-sport venues).
        var pitches = PitchSizes.ResolvedPitches(venue);
        PitchDto? pitch;
        if (!string.IsNullOrEmpty(req.PitchId))
        {
            pitch = pitches.FirstOrDefault(p => p.Id == req.PitchId);
            if (pitch == null)
                return BadRequest(new ApiResponse<object> { Success = false, Message = "Pitch does not belong to this venue" });
        }
        else
        {
            if (pitches.Count != 1)
                return BadRequest(new ApiResponse<object> { Success = false, Message = "PITCH_REQUIRED — venue has multiple pitches; pass pitchId" });
            pitch = pitches[0];
        }

        // 4. Validate pitch-size.
        string? pitchSize = null;
        if (pitch.ParentSize != null)
        {
            pitchSize = req.PitchSize ?? pitch.ParentSize;
            var offered = PitchSizes.OfferedSizes(pitch);
            if (!offered.Contains(pitchSize))
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = $"Pitch '{pitch.Name}' does not offer {pitchSize}-aside. Offered: {string.Join(", ", offered)}"
                });
        }
        else if (!string.IsNullOrEmpty(req.PitchSize))
        {
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This pitch is not subdividable; omit pitchSize" });
        }

        // 5. Operating-hours check (overnight-aware): anchor the permanent on a real
        //    date that falls on the requested weekday, then validate the window.
        var anchor = NextDateForWeekday(req.DayOfWeek);
        var hoursError = ValidateAgainstOperatingHours(venue, pitch, anchor, startTimeSpan, req.Duration);
        if (hoursError != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = hoursError });

        // 6. Capacity check vs. (a) other active permanents on same pitch + day,
        //    and (b) future Booking rows on dates falling on the same weekday
        //    in the next 90 days. Both feed into the same capacity-unit reducer.
        var capacityError = await CheckCapacityForCreate(venue, pitch, pitchSize, req.DayOfWeek, startTimeSpan, req.Duration);
        if (capacityError != null)
            return Conflict(new ApiResponse<object> { Success = false, Message = capacityError });

        var perm = new PermanentBooking
        {
            VenueId = venueId,
            PitchId = IsLegacyPitchId(pitch.Id) ? null : pitch.Id,
            PitchSize = pitchSize,
            DayOfWeek = req.DayOfWeek,
            StartTime = req.StartTime,
            Duration = req.Duration,
            Label = string.IsNullOrWhiteSpace(req.Label) ? null : req.Label.Trim(),
            Status = "active",
            CreatedByUserId = UserId,
        };
        _db.PermanentBookings.Add(perm);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<PermanentBookingDto>
        {
            Data = ToDto(perm),
            Message = "Permanent booking created"
        });
    }

    // PATCH /api/v1/permanent-bookings/{id}/cancel
    [HttpPatch("api/v1/permanent-bookings/{id}/cancel")]
    public async Task<IActionResult> Cancel(string id)
    {
        var perm = await _db.PermanentBookings
            .Include(p => p.Venue)
            .FirstOrDefaultAsync(p => p.Id == id);
        if (perm == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Permanent booking not found" });

        if (!CanManageVenue(perm.Venue))
            return Forbid();

        if (perm.Status == "cancelled")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Already cancelled" });

        perm.Status = "cancelled";
        perm.CancelledAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<PermanentBookingDto>
        {
            Data = ToDto(perm),
            Message = "Permanent booking cancelled"
        });
    }

    // DELETE /api/v1/permanent-bookings/{id}  (super_admin only)
    [HttpDelete("api/v1/permanent-bookings/{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (UserRole != "super_admin")
            return Forbid();

        var perm = await _db.PermanentBookings.FindAsync(id);
        if (perm == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Permanent booking not found" });

        _db.PermanentBookings.Remove(perm);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object> { Data = null, Message = "Permanent booking deleted" });
    }

    // ---- Helpers ----

    private bool CanManageVenue(Venue venue)
    {
        if (UserRole == "super_admin") return true;
        if (UserRole == "venue_owner" && venue.OwnerId == UserId) return true;
        return false;
    }

    /// <summary>Find the next date (today or later) whose DayOfWeek matches.</summary>
    private static DateTime NextDateForWeekday(int dow)
    {
        var today = DateTime.UtcNow.Date;
        var diff = ((dow - (int)today.DayOfWeek) + 7) % 7;
        return today.AddDays(diff);
    }

    /// <summary>
    /// Validate that <c>start + duration</c> fits inside the pitch's (or venue's)
    /// operating hours for the given anchor date. Mirrors the overnight-aware
    /// rule used by <c>BookingsController.CreateBooking</c>: when the close time
    /// is earlier than open, treat it as crossing midnight.
    /// </summary>
    private static string? ValidateAgainstOperatingHours(
        Venue venue, PitchDto pitch, DateTime anchorDate, TimeSpan startTime, int durationMinutes)
    {
        var dayShort = anchorDate.DayOfWeek.ToString().ToLower()[..3];
        var dayFull = anchorDate.DayOfWeek.ToString().ToLower();

        Dictionary<string, object>? hoursMap = venue.OperatingHours;
        if (pitch.OperatingHours is Dictionary<string, object> pitchHoursDict)
            hoursMap = pitchHoursDict;
        else if (pitch.OperatingHours != null)
        {
            try
            {
                var json = JsonSerializer.Serialize(pitch.OperatingHours);
                hoursMap = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? venue.OperatingHours;
            }
            catch { /* fall through */ }
        }

        if (hoursMap == null) return null; // no hours configured → permissive

        if (!hoursMap.TryGetValue(dayFull, out var dayHoursObj) &&
            !hoursMap.TryGetValue(dayShort, out dayHoursObj))
            return null;

        var dayHoursJson = JsonSerializer.Serialize(dayHoursObj);
        var dayHours = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dayHoursJson);
        if (dayHours == null) return null;

        if (dayHours.TryGetValue("closed", out var closedEl) && closedEl.ValueKind == JsonValueKind.True)
            return "Venue is closed on that day";

        string GetStr(string k, string fallback)
            => dayHours.TryGetValue(k, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;

        if (!TimeSpan.TryParse(GetStr("open", "00:00"), out var openTime) ||
            !TimeSpan.TryParse(GetStr("close", "23:59"), out var closeTime))
            return null;

        var endTime = startTime + TimeSpan.FromMinutes(durationMinutes);
        // Overnight wrap: close <= open means the venue closes the following day.
        if (closeTime <= openTime) closeTime += TimeSpan.FromHours(24);
        // If the booking starts before open and would only fit by wrapping, reject.
        if (startTime < openTime || endTime > closeTime)
            return $"Booking must be within operating hours ({GetStr("open", "00:00")} - {GetStr("close", "23:59")})";
        return null;
    }

    /// <summary>
    /// Run capacity-unit math for the new permanent against:
    ///   1. existing active permanents on the same pitch + same day-of-week, and
    ///   2. future Booking rows scheduled for the same weekday in the next 90 days.
    /// Returns an error message (HTTP 409 caller) when capacity would be exceeded
    /// on at least one matching slot, or null when the slot is free.
    /// </summary>
    private async Task<string?> CheckCapacityForCreate(
        Venue venue, PitchDto pitch, string? pitchSize, int dow, TimeSpan startTime, int durationMinutes)
    {
        var endTime = startTime + TimeSpan.FromMinutes(durationMinutes);
        var capacity = PitchSizes.CapacityOf(pitch);
        var requestedWeight = pitchSize != null ? PitchSizes.WeightOf(pitchSize) : 1;
        var isSubdividable = pitch.ParentSize != null && (pitch.SubSizes?.Count ?? 0) > 0;

        // (a) Overlapping active permanents on the same pitch + same weekday.
        var sameDayPerms = await _db.PermanentBookings
            .Where(p => p.VenueId == venue.Id && p.DayOfWeek == dow && p.Status == "active")
            .ToListAsync();
        var overlappingPerms = sameDayPerms
            .Where(p => PermanentOnPitch(p, venue, pitch))
            .Where(p =>
            {
                if (!TimeSpan.TryParse(p.StartTime, out var s)) return false;
                var e = s + TimeSpan.FromMinutes(p.Duration);
                return startTime < e && endTime > s;
            })
            .ToList();

        // (b) Future Booking rows on dates whose DayOfWeek == dow within 90 days.
        var today = DateTime.UtcNow.Date;
        var horizon = today.AddDays(90);
        var futureBookings = await _db.Bookings
            .Where(b => b.VenueId == venue.Id
                && b.Date >= today
                && b.Date < horizon
                && b.Status != "cancelled"
                && b.StartTime != null)
            .ToListAsync();
        var overlappingBookings = futureBookings
            .Where(b => (int)b.Date.DayOfWeek == dow)
            .Where(b => BookingOnPitch(b, venue, pitch))
            .Where(b =>
            {
                if (!TimeSpan.TryParse(b.StartTime, out var s)) return false;
                var e = s + TimeSpan.FromMinutes(b.Duration);
                return startTime < e && endTime > s;
            })
            .ToList();

        if (isSubdividable)
        {
            var permUnits = overlappingPerms.Sum(p => PitchSizes.WeightOf(p.PitchSize ?? pitch.ParentSize));
            // Aggregate booked units per occurrence date (a permanent must fit on
            // every matching weekday). Take the max per-date used.
            var maxBookedUnits = overlappingBookings
                .GroupBy(b => b.Date.Date)
                .Select(g => g.Sum(b => PitchSizes.WeightOf(b.PitchSize ?? pitch.ParentSize)))
                .DefaultIfEmpty(0)
                .Max();
            var used = permUnits + maxBookedUnits;
            if (used + requestedWeight > capacity)
                return $"Slot conflicts with existing reservations. {capacity - used} of {capacity} units remain.";
        }
        else
        {
            if (overlappingPerms.Count > 0 || overlappingBookings.Count > 0)
                return "Slot already reserved on this pitch.";
        }
        return null;
    }

    /// <summary>Mirrors <c>BookingsController.BookingOnPitch</c> for permanents.</summary>
    private static bool PermanentOnPitch(PermanentBooking p, Venue v, PitchDto pitch)
    {
        if (!string.IsNullOrEmpty(p.PitchId))
            return p.PitchId == pitch.Id;
        var firstOfSport = PitchSizes.ResolvedPitches(v)
            .FirstOrDefault(x => string.Equals(x.Sport, pitch.Sport, StringComparison.OrdinalIgnoreCase));
        return firstOfSport != null && firstOfSport.Id == pitch.Id;
    }

    /// <summary>Mirrors <c>BookingsController.BookingOnPitch</c>.</summary>
    private static bool BookingOnPitch(Booking b, Venue v, PitchDto pitch)
    {
        if (!string.IsNullOrEmpty(b.PitchId))
            return b.PitchId == pitch.Id;
        var firstOfSport = PitchSizes.ResolvedPitches(v)
            .FirstOrDefault(x => string.Equals(x.Sport, b.Sport, StringComparison.OrdinalIgnoreCase));
        return firstOfSport != null && firstOfSport.Id == pitch.Id;
    }

    private static bool IsLegacyPitchId(string? id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("legacy-", StringComparison.Ordinal);

    private static PermanentBookingDto ToDto(PermanentBooking p) => new()
    {
        Id = p.Id,
        VenueId = p.VenueId,
        PitchId = p.PitchId,
        PitchSize = p.PitchSize,
        DayOfWeek = p.DayOfWeek,
        StartTime = p.StartTime,
        Duration = p.Duration,
        Label = p.Label,
        Status = p.Status,
        CreatedByUserId = p.CreatedByUserId,
        CreatedAt = p.CreatedAt,
        CancelledAt = p.CancelledAt,
    };
}
