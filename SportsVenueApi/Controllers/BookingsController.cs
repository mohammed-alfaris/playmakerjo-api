using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/bookings")]
[Authorize]
public class BookingsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly SettingsService _settings;
    private readonly ILogger<BookingsController> _logger;
    private readonly string _uploadsBaseUrl;

    public BookingsController(
        AppDbContext db,
        NotificationService notifications,
        SettingsService settings,
        ILogger<BookingsController> logger,
        IConfiguration config)
    {
        _db = db;
        _notifications = notifications;
        _settings = settings;
        _logger = logger;
        _uploadsBaseUrl = config["Uploads:BaseUrl"]?.TrimEnd('/') ?? "";
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    // GET /api/v1/bookings — admin/owner list (existing, for dashboard)
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? venue_id = null,
        [FromQuery] string? pitch_id = null,
        [FromQuery] string? owner_id = null,
        [FromQuery(Name = "from")] string? fromDate = null,
        [FromQuery(Name = "to")] string? toDate = null)
    {
        var ownerId = owner_id;
        if (UserRole == "venue_owner")
            ownerId = UserId;

        var baseQuery = _db.Bookings.AsQueryable();

        if (!string.IsNullOrEmpty(ownerId))
            baseQuery = baseQuery.Where(b => b.Venue.OwnerId == ownerId);

        if (!string.IsNullOrEmpty(status))
            baseQuery = baseQuery.Where(b => b.Status == status);

        if (!string.IsNullOrEmpty(venue_id))
            baseQuery = baseQuery.Where(b => b.VenueId == venue_id);

        // Pitch filter. Synthetic legacy pitch IDs ("legacy-{venueId}-{sport}")
        // never hit the DB — they map to "rows on that venue+sport whose
        // pitch_id is NULL", which is the server-side projection contract.
        if (!string.IsNullOrEmpty(pitch_id))
        {
            if (IsLegacyPitchId(pitch_id))
            {
                // Expected format: "legacy-{venueId}-{sport}" — the last dash
                // segment is the sport name.
                var tail = pitch_id.Substring("legacy-".Length);
                var dash = tail.LastIndexOf('-');
                if (dash > 0)
                {
                    var legacyVenueId = tail.Substring(0, dash);
                    var legacySport = tail.Substring(dash + 1);
                    baseQuery = baseQuery.Where(b =>
                        b.VenueId == legacyVenueId &&
                        b.Sport == legacySport &&
                        b.PitchId == null);
                }
                else
                {
                    // Malformed legacy id — return no rows rather than the full set.
                    baseQuery = baseQuery.Where(b => false);
                }
            }
            else
            {
                baseQuery = baseQuery.Where(b => b.PitchId == pitch_id);
            }
        }

        if (!string.IsNullOrEmpty(fromDate) && DateTime.TryParse(fromDate, out var from))
            baseQuery = baseQuery.Where(b => b.Date >= from);

        if (!string.IsNullOrEmpty(toDate) && DateTime.TryParse(toDate, out var to))
            baseQuery = baseQuery.Where(b => b.Date <= to);

        var total = await baseQuery.CountAsync();
        var bookings = await baseQuery
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .OrderByDescending(b => b.Date)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = bookings.Select(b => ToDto(b)).ToList();

        return Ok(new ApiResponse<List<BookingResponse>>
        {
            Data = data,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    // GET /api/v1/bookings/my — player's own bookings
    [HttpGet("my")]
    public async Task<IActionResult> MyBookings(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? status = null)
    {
        var baseQuery = _db.Bookings.Where(b => b.PlayerId == UserId);

        if (!string.IsNullOrEmpty(status))
            baseQuery = baseQuery.Where(b => b.Status == status);

        var total = await baseQuery.CountAsync();
        var bookings = await baseQuery
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .OrderByDescending(b => b.Date)
            .ThenByDescending(b => b.StartTime)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var data = bookings.Select(b => ToDto(b)).ToList();

        return Ok(new ApiResponse<List<BookingResponse>>
        {
            Data = data,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    // GET /api/v1/bookings/{id} — single booking detail
    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        // Players can only see their own bookings
        // Owners can see bookings for their venues
        // Admins can see all
        if (UserRole == "player" && booking.PlayerId != UserId)
            return Forbid();

        if (UserRole == "venue_owner" && booking.Venue.OwnerId != UserId)
            return Forbid();

        return Ok(new ApiResponse<BookingResponse> { Data = ToDto(booking, includeFullProof: true) });
    }

    // POST /api/v1/bookings — create a new booking
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBookingRequest req)
    {
        // Validate venue exists and is active
        var venue = await _db.Venues.FindAsync(req.VenueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (venue.Status != "active")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Venue is not active" });

        // Validate sport is offered by venue
        if (!venue.Sports.Contains(req.Sport, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Venue does not offer {req.Sport}" });

        // Validate date is not in the past
        if (!DateTime.TryParse(req.Date, out var bookingDate))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid date format. Use YYYY-MM-DD" });

        if (bookingDate.Date < DateTime.UtcNow.Date)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Cannot book in the past" });

        // Validate start time format
        if (string.IsNullOrEmpty(req.StartTime) || !TimeSpan.TryParse(req.StartTime, out var startTimeSpan))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid start time format. Use HH:mm" });

        // Validate duration
        if (req.Duration < venue.MinBookingDuration || req.Duration > venue.MaxBookingDuration)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = $"Duration must be between {venue.MinBookingDuration} and {venue.MaxBookingDuration} minutes"
            });

        // Check operating hours for the day
        var dayName = bookingDate.DayOfWeek.ToString().ToLower()[..3];  // "mon", "tue", etc.
        var operatingHours = venue.OperatingHours;
        if (operatingHours != null && operatingHours.TryGetValue(dayName, out var dayHoursObj))
        {
            var dayHoursJson = JsonSerializer.Serialize(dayHoursObj);
            var dayHours = JsonSerializer.Deserialize<Dictionary<string, string>>(dayHoursJson);
            if (dayHours != null)
            {
                if (TimeSpan.TryParse(dayHours.GetValueOrDefault("open", "00:00"), out var openTime) &&
                    TimeSpan.TryParse(dayHours.GetValueOrDefault("close", "23:59"), out var closeTime))
                {
                    var endTimeSpan = startTimeSpan + TimeSpan.FromMinutes(req.Duration);
                    if (startTimeSpan < openTime || endTimeSpan > closeTime)
                        return BadRequest(new ApiResponse<object>
                        {
                            Success = false,
                            Message = $"Booking must be within operating hours ({dayHours["open"]} - {dayHours["close"]})"
                        });
                }
            }
        }

        // Resolve which pitch this booking lives on.
        var pitchResolution = ResolvePitchForBooking(venue, req.PitchId, req.Sport);
        if (pitchResolution.Error != null)
            return pitchResolution.Status == 404
                ? NotFound(new ApiResponse<object> { Success = false, Message = pitchResolution.Error })
                : BadRequest(new ApiResponse<object> { Success = false, Message = pitchResolution.Error });
        var pitch = pitchResolution.Pitch!;

        // Pitch-size validation. Subdivision is football-only and carried on the pitch.
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

        // Overlap detection is scoped per-pitch. A booking on Pitch 1 never blocks
        // Pitch 2. Inside the pitch, subdividable pitches use the capacity-unit pool
        // (e.g. 11-aside = 4 units, 8 = 2, 6 = 1), non-subdividable pitches use the
        // naive any-overlap rule.
        var endTime = startTimeSpan + TimeSpan.FromMinutes(req.Duration);
        var sameDayBookings = await _db.Bookings
            .Where(b => b.VenueId == req.VenueId
                && b.Date.Date == bookingDate.Date
                && b.Status != "cancelled"
                && b.StartTime != null)
            .ToListAsync();
        var pitchBookings = sameDayBookings
            .Where(b => BookingOnPitch(b, venue, pitch))
            .ToList();

        // Owner-managed permanents matching this date's weekday block the slot
        // exactly like a real booking would. They never produce a Booking row, so
        // we feed them straight into the same capacity-unit reducer.
        var dow = (int)bookingDate.DayOfWeek;
        var sameDayPermanents = await _db.PermanentBookings
            .Where(p => p.VenueId == req.VenueId
                && p.Status == "active"
                && p.DayOfWeek == dow)
            .ToListAsync();
        var pitchPermanents = sameDayPermanents
            .Where(p => PermanentOnPitch(p, venue, pitch))
            .ToList();

        var capacity = PitchSizes.CapacityOf(pitch);
        var requestedWeight = pitchSize != null ? PitchSizes.WeightOf(pitchSize) : 1;
        var isSubdividable = pitch.ParentSize != null && (pitch.SubSizes?.Count ?? 0) > 0;

        var overlapping = new List<Booking>();
        foreach (var existing in pitchBookings)
        {
            if (!TimeSpan.TryParse(existing.StartTime, out var existingStart)) continue;
            var existingEnd = existingStart + TimeSpan.FromMinutes(existing.Duration);
            if (startTimeSpan < existingEnd && endTime > existingStart)
                overlapping.Add(existing);
        }
        var overlappingPerms = new List<PermanentBooking>();
        foreach (var perm in pitchPermanents)
        {
            if (!TimeSpan.TryParse(perm.StartTime, out var permStart)) continue;
            var permEnd = permStart + TimeSpan.FromMinutes(perm.Duration);
            if (startTimeSpan < permEnd && endTime > permStart)
                overlappingPerms.Add(perm);
        }

        if (isSubdividable)
        {
            var usedUnits = overlapping.Sum(b => PitchSizes.WeightOf(b.PitchSize ?? pitch.ParentSize))
                          + overlappingPerms.Sum(p => PitchSizes.WeightOf(p.PitchSize ?? pitch.ParentSize));
            if (usedUnits + requestedWeight > capacity)
            {
                var remaining = capacity - usedUnits;
                return Conflict(new ApiResponse<object>
                {
                    Success = false,
                    Message = $"This size is not available at that time. {remaining} of {capacity} units remain on {pitch.Name}."
                });
            }
        }
        else if (overlapping.Count > 0 || overlappingPerms.Count > 0)
        {
            if (overlapping.Count > 0)
            {
                var first = overlapping[0];
                var existingStart = TimeSpan.Parse(first.StartTime!);
                var existingEnd = existingStart + TimeSpan.FromMinutes(first.Duration);
                return Conflict(new ApiResponse<object>
                {
                    Success = false,
                    Message = $"Time slot conflicts with an existing booking on {pitch.Name} ({first.StartTime} - {existingEnd:hh\\:mm})"
                });
            }
            else
            {
                var first = overlappingPerms[0];
                var permStart = TimeSpan.Parse(first.StartTime);
                var permEnd = permStart + TimeSpan.FromMinutes(first.Duration);
                return Conflict(new ApiResponse<object>
                {
                    Success = false,
                    Message = $"Time slot conflicts with a recurring reservation on {pitch.Name} ({first.StartTime} - {permEnd:hh\\:mm})"
                });
            }
        }

        // Reject card payments (coming soon)
        if (req.PaymentMethod == "stripe")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Card payments coming soon. Please use CliQ." });

        // Manual (walk-in) bookings: only super_admin or the venue's owner may create them.
        // They skip the payment flow entirely — confirmed immediately, 0% platform fee.
        var isManual = req.IsManual;
        if (isManual)
        {
            var allowed = UserRole == "super_admin" || (UserRole == "venue_owner" && venue.OwnerId == UserId);
            if (!allowed)
                return Forbid();
        }

        // Calculate amounts + revenue split (platform fee read from settings).
        // Price resolution order, pitch-scoped:
        //   1. pitch.sizePrices[pitchSize]  (per-pitch, per-size)
        //   2. pitch.pricePerHour           (per-pitch default)
        //   3. venue.pricePerHour           (venue-level fallback)
        var platformFee = await _settings.GetPlatformFeePercentageAsync();
        double hourlyPrice;
        if (pitchSize != null && pitch.SizePrices.TryGetValue(pitchSize, out var perSize))
            hourlyPrice = perSize;
        else if (pitch.PricePerHour > 0)
            hourlyPrice = pitch.PricePerHour;
        else
            hourlyPrice = venue.PricePerHour;
        var totalAmount = hourlyPrice * req.Duration / 60.0;
        var depositAmount = totalAmount * (venue.DepositPercentage / 100.0);
        var systemFeePercentage = isManual ? 0.0 : platformFee;
        var systemFee = isManual ? 0.0 : totalAmount * (platformFee / 100.0);
        var ownerAmount = totalAmount - systemFee;

        var booking = new Booking
        {
            VenueId = req.VenueId,
            PlayerId = UserId,
            Sport = req.Sport,
            PitchId = IsLegacyPitchId(pitch.Id) ? null : pitch.Id,
            PitchSize = pitchSize,
            Date = bookingDate,
            StartTime = req.StartTime,
            Duration = req.Duration,
            Amount = totalAmount,
            TotalAmount = totalAmount,
            DepositAmount = depositAmount,
            SystemFeePercentage = systemFeePercentage,
            SystemFee = systemFee,
            OwnerAmount = ownerAmount,
            PaymentMethod = req.PaymentMethod,
            Notes = req.Notes,
            Status = isManual ? "confirmed" : "pending_payment",
            DepositPaid = isManual,
            AmountPaid = isManual ? totalAmount : 0,
        };

        _db.Bookings.Add(booking);
        await _db.SaveChangesAsync();

        // Reload with includes
        booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstAsync(b => b.Id == booking.Id);

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Booking created successfully"
        });
    }

    // PATCH /api/v1/bookings/{id}/cancel — cancel a booking
    [HttpPatch("{id}/cancel")]
    public async Task<IActionResult> Cancel(string id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        // Players can cancel their own bookings
        // Owners can cancel bookings for their venues
        // Admins can cancel any
        if (UserRole == "player" && booking.PlayerId != UserId)
            return Forbid();

        if (UserRole == "venue_owner" && booking.Venue.OwnerId != UserId)
            return Forbid();

        // Can only cancel pending/confirmed bookings
        if (booking.Status == "cancelled")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Booking is already cancelled" });

        if (booking.Status == "completed")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Cannot cancel a completed booking" });

        if (booking.Status == "no_show")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Cannot cancel a no-show booking" });

        booking.Status = "cancelled";
        await _db.SaveChangesAsync();

        // Notify player + owner about cancellation (non-blocking)
        try { await _notifications.NotifyBookingCancelled(booking, UserId); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification failed"); }

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Booking cancelled successfully"
        });
    }

    // PATCH /api/v1/bookings/{id}/complete — mark a confirmed booking as completed
    [HttpPatch("{id}/complete")]
    public async Task<IActionResult> Complete(string id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (UserRole == "player")
            return Forbid();

        if (UserRole == "venue_owner" && booking.Venue.OwnerId != UserId)
            return Forbid();

        if (booking.Status != "confirmed")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Only confirmed bookings can be marked as completed" });

        booking.Status = "completed";
        await _db.SaveChangesAsync();

        try { await _notifications.NotifyBookingCompleted(booking); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification failed"); }

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Booking marked as completed"
        });
    }

    // PATCH /api/v1/bookings/{id}/no-show — mark a confirmed booking as no-show
    [HttpPatch("{id}/no-show")]
    public async Task<IActionResult> NoShow(string id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (UserRole == "player")
            return Forbid();

        if (UserRole == "venue_owner" && booking.Venue.OwnerId != UserId)
            return Forbid();

        if (booking.Status != "confirmed")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Only confirmed bookings can be marked as no-show" });

        booking.Status = "no_show";
        await _db.SaveChangesAsync();

        try { await _notifications.NotifyNoShow(booking); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification failed"); }

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Booking marked as no-show"
        });
    }

    // PATCH /api/v1/bookings/{id}/pay-card — TEMPORARILY DISABLED (CliQ only)
    [HttpPatch("{id}/pay-card")]
    public async Task<IActionResult> PayWithCard(string id)
    {
        return BadRequest(new ApiResponse<object> { Success = false, Message = "Card payments coming soon. Please use CliQ." });

        #pragma warning disable CS0162
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (booking.PlayerId != UserId)
            return Forbid();

        if (booking.Status != "pending_payment")
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Cannot pay for a booking with status '{booking.Status}'" });

        if (booking.PaymentMethod != "stripe")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This booking is not set for card payment" });

        // Simulate card payment — auto-confirm, full amount charged
        booking.DepositPaid = true;
        booking.AmountPaid = booking.TotalAmount;
        booking.Status = "confirmed";
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Payment successful. Booking confirmed!"
        });
        #pragma warning restore CS0162
    }

    // PATCH /api/v1/bookings/{id}/upload-proof — player uploads CliQ payment proof
    [HttpPatch("{id}/upload-proof")]
    public async Task<IActionResult> UploadProof(string id, [FromBody] UploadProofRequest req)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (booking.PlayerId != UserId)
            return Forbid();

        if (booking.Status != "pending_payment")
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Cannot upload proof for a booking with status '{booking.Status}'" });

        if (booking.PaymentMethod != "cliq")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This booking is not set for CliQ payment" });

        if (string.IsNullOrEmpty(req.PaymentProof))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Payment proof image is required" });

        booking.PaymentProof = req.PaymentProof;
        booking.PaymentProofStatus = "pending_review";
        booking.Status = "pending_review";
        await _db.SaveChangesAsync();

        // Notify venue owner about new proof (non-blocking)
        try { await _notifications.NotifyProofReceived(booking); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification failed"); }

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Payment proof uploaded. Waiting for venue owner approval."
        });
    }

    // PATCH /api/v1/bookings/{id}/review-proof — owner approves/rejects CliQ proof
    [HttpPatch("{id}/review-proof")]
    public async Task<IActionResult> ReviewProof(string id, [FromBody] ReviewProofRequest req)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        // Only venue owner or admin can review
        if (UserRole == "venue_owner" && booking.Venue.OwnerId != UserId)
            return Forbid();

        if (UserRole == "player")
            return Forbid();

        if (booking.PaymentProofStatus != "pending_review")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "No proof pending review" });

        if (req.Approved)
        {
            booking.PaymentProofStatus = "approved";
            booking.DepositPaid = true;
            booking.AmountPaid = booking.DepositAmount;
            booking.Status = "confirmed";
            booking.PaymentProofNote = null;
        }
        else
        {
            booking.PaymentProofStatus = "rejected";
            booking.PaymentProofNote = req.Note ?? "Payment proof was rejected";
            booking.Status = "pending_payment";  // back to pending so player can re-upload
            booking.PaymentProof = null;  // clear the rejected proof
        }

        await _db.SaveChangesAsync();

        // Notify player about proof review result (non-blocking — don't fail the request)
        try
        {
            if (req.Approved)
                await _notifications.NotifyProofApproved(booking);
            else
                await _notifications.NotifyProofRejected(booking, req.Note);
        }
        catch (Exception ex)
        {
            // Log but don't fail the review action
            _logger.LogWarning(ex, "Notification failed");
        }

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = req.Approved ? "Proof approved. Booking confirmed!" : "Proof rejected."
        });
    }

    // PATCH /api/v1/bookings/{id}/confirm — legacy/admin confirm endpoint
    [HttpPatch("{id}/confirm")]
    public async Task<IActionResult> Confirm(string id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (UserRole == "venue_owner" && booking.Venue.OwnerId != UserId)
            return Forbid();

        if (UserRole == "player" && booking.PlayerId != UserId)
            return Forbid();

        if (booking.Status != "pending" && booking.Status != "pending_payment")
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Cannot confirm a booking with status '{booking.Status}'" });

        booking.Status = "confirmed";
        booking.DepositPaid = true;
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Booking confirmed"
        });
    }

    // POST /api/v1/bookings/recurring — create a recurring series
    [HttpPost("recurring")]
    public async Task<IActionResult> CreateRecurring([FromBody] CreateRecurringBookingRequest req)
    {
        var venue = await _db.Venues.FindAsync(req.VenueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });
        if (venue.Status != "active")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Venue is not active" });
        if (!venue.Sports.Contains(req.Sport, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Venue does not offer {req.Sport}" });

        if (!DateTime.TryParse(req.StartDate, out var startDate) ||
            !DateTime.TryParse(req.EndDate, out var endDate))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid date format. Use YYYY-MM-DD" });

        startDate = startDate.Date;
        endDate = endDate.Date;

        if (startDate < DateTime.UtcNow.Date)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Start date cannot be in the past" });
        if (endDate <= startDate)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "End date must be after start date" });
        if (endDate > startDate.AddMonths(3))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Recurring series cannot exceed 3 months" });

        if (string.IsNullOrEmpty(req.StartTime) || !TimeSpan.TryParse(req.StartTime, out var startTimeSpan))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid start time format. Use HH:mm" });

        if (req.Duration < venue.MinBookingDuration || req.Duration > venue.MaxBookingDuration)
            return BadRequest(new ApiResponse<object>
            {
                Success = false,
                Message = $"Duration must be between {venue.MinBookingDuration} and {venue.MaxBookingDuration} minutes"
            });

        var recurType = (req.RecurrenceType ?? "weekly").ToLower();
        if (recurType != "weekly" && recurType != "biweekly")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "recurrenceType must be 'weekly' or 'biweekly'" });

        var stepDays = recurType == "biweekly" ? 14 : 7;
        var policy = (req.ConflictPolicy ?? "skip").ToLower();

        // Resolve which pitch this recurring series will live on (same rules as
        // one-off bookings: explicit pitchId if given, single-match-sport otherwise,
        // else PITCH_REQUIRED).
        var pitchResolution = ResolvePitchForBooking(venue, req.PitchId, req.Sport);
        if (pitchResolution.Error != null)
            return pitchResolution.Status == 404
                ? NotFound(new ApiResponse<object> { Success = false, Message = pitchResolution.Error })
                : BadRequest(new ApiResponse<object> { Success = false, Message = pitchResolution.Error });
        var pitch = pitchResolution.Pitch!;

        // Pitch-size validation. Subdivision is football-only and carried on the pitch.
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

        // Compute occurrence dates
        var occurrences = new List<DateTime>();
        for (var d = startDate; d <= endDate; d = d.AddDays(stepDays))
            occurrences.Add(d);

        if (occurrences.Count == 0)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "No occurrences in the selected range" });

        // Validate operating hours for that day-of-week (same every week).
        // Pitch-level operating_hours (when set) take precedence over the venue's.
        var endTime = startTimeSpan + TimeSpan.FromMinutes(req.Duration);
        var dayName = startDate.DayOfWeek.ToString().ToLower()[..3];
        Dictionary<string, object>? operatingHours = null;
        if (pitch.OperatingHours is Dictionary<string, object> pitchHoursDict)
            operatingHours = pitchHoursDict;
        else if (pitch.OperatingHours != null)
        {
            try
            {
                var json = JsonSerializer.Serialize(pitch.OperatingHours);
                operatingHours = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            }
            catch { /* fall through to venue hours */ }
        }
        operatingHours ??= venue.OperatingHours;
        if (operatingHours != null && operatingHours.TryGetValue(dayName, out var dayHoursObj))
        {
            var dayHoursJson = JsonSerializer.Serialize(dayHoursObj);
            var dayHours = JsonSerializer.Deserialize<Dictionary<string, string>>(dayHoursJson);
            if (dayHours != null &&
                TimeSpan.TryParse(dayHours.GetValueOrDefault("open", "00:00"), out var openTime) &&
                TimeSpan.TryParse(dayHours.GetValueOrDefault("close", "23:59"), out var closeTime))
            {
                if (startTimeSpan < openTime || endTime > closeTime)
                    return BadRequest(new ApiResponse<object>
                    {
                        Success = false,
                        Message = $"Booking must be within operating hours ({dayHours["open"]} - {dayHours["close"]})"
                    });
            }
        }

        // Pre-load candidate bookings in the date range. Scope to the same pitch
        // only — bookings on other pitches never block this one. Load everything
        // same-venue/same-range then filter in-memory via BookingOnPitch so we
        // pick up legacy rows (pitch_id = NULL) that resolve to this pitch.
        var existingAll = await _db.Bookings
            .Where(b => b.VenueId == req.VenueId
                && b.Date >= startDate
                && b.Date <= endDate
                && b.Status != "cancelled"
                && b.StartTime != null)
            .ToListAsync();
        var existing = existingAll.Where(b => BookingOnPitch(b, venue, pitch)).ToList();

        // Owner-managed permanents that fire on the same weekday as this series.
        // A recurring series on Sunday only has to dodge permanents on Sunday.
        var seriesDow = (int)startDate.DayOfWeek;
        var permanentsAll = await _db.PermanentBookings
            .Where(p => p.VenueId == req.VenueId
                && p.Status == "active"
                && p.DayOfWeek == seriesDow)
            .ToListAsync();
        var permanents = permanentsAll.Where(p => PermanentOnPitch(p, venue, pitch)).ToList();

        // Capacity-unit conflict detection inside the pitch: subdividable pitches
        // sum overlapping unit weights against the pitch's capacity; non-subdividable
        // pitches fall back to naive any-overlap.
        var capacity = PitchSizes.CapacityOf(pitch);
        var requestedWeight = pitchSize != null ? PitchSizes.WeightOf(pitchSize) : 1;
        var isSubdividable = pitch.ParentSize != null && (pitch.SubSizes?.Count ?? 0) > 0;

        // Pre-compute permanent overlaps once (same weekday + time-window doesn't
        // depend on the specific date).
        var permOverlaps = permanents.Where(p =>
        {
            if (!TimeSpan.TryParse(p.StartTime, out var ps)) return false;
            var pe = ps + TimeSpan.FromMinutes(p.Duration);
            return startTimeSpan < pe && endTime > ps;
        }).ToList();
        var permUnits = permOverlaps.Sum(p => PitchSizes.WeightOf(p.PitchSize ?? pitch.ParentSize));

        var conflictDates = new List<DateTime>();
        var validDates = new List<DateTime>();
        foreach (var date in occurrences)
        {
            var overlaps = new List<Booking>();
            foreach (var ex in existing.Where(e => e.Date.Date == date.Date))
            {
                if (TimeSpan.TryParse(ex.StartTime, out var exStart))
                {
                    var exEnd = exStart + TimeSpan.FromMinutes(ex.Duration);
                    if (startTimeSpan < exEnd && endTime > exStart)
                        overlaps.Add(ex);
                }
            }

            bool conflict;
            if (isSubdividable)
            {
                var usedUnits = overlaps.Sum(b => PitchSizes.WeightOf(b.PitchSize ?? pitch.ParentSize)) + permUnits;
                conflict = usedUnits + requestedWeight > capacity;
            }
            else
            {
                conflict = overlaps.Count > 0 || permOverlaps.Count > 0;
            }

            if (conflict) conflictDates.Add(date);
            else validDates.Add(date);
        }

        if (policy == "fail" && conflictDates.Count > 0)
        {
            return Conflict(new ApiResponse<object>
            {
                Success = false,
                Message = $"{conflictDates.Count} occurrence(s) conflict with existing bookings",
                Data = new { conflictingDates = conflictDates.Select(d => d.ToString("yyyy-MM-dd")).ToList() }
            });
        }

        if (validDates.Count == 0)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "All occurrences conflict with existing bookings" });

        var platformFee = await _settings.GetPlatformFeePercentageAsync();
        // Pitch-scoped price resolution (same order as one-off bookings):
        //   1. pitch.sizePrices[pitchSize]
        //   2. pitch.pricePerHour
        //   3. venue.pricePerHour
        double hourlyPrice;
        if (pitchSize != null && pitch.SizePrices.TryGetValue(pitchSize, out var perSize))
            hourlyPrice = perSize;
        else if (pitch.PricePerHour > 0)
            hourlyPrice = pitch.PricePerHour;
        else
            hourlyPrice = venue.PricePerHour;
        var totalAmount = hourlyPrice * req.Duration / 60.0;
        var depositAmount = totalAmount * (venue.DepositPercentage / 100.0);
        var systemFee = totalAmount * (platformFee / 100.0);
        var ownerAmount = totalAmount - systemFee;

        var group = new RecurringBookingGroup
        {
            PlayerId = UserId,
            VenueId = req.VenueId,
            Sport = req.Sport,
            DayOfWeek = (int)startDate.DayOfWeek,
            StartTime = req.StartTime,
            Duration = req.Duration,
            RecurrenceType = recurType,
            StartDate = startDate,
            EndDate = endDate,
            Status = "active",
        };

        await using var tx = await _db.Database.BeginTransactionAsync();
        _db.RecurringBookingGroups.Add(group);

        var newBookings = validDates.Select(date => new Booking
        {
            VenueId = req.VenueId,
            PlayerId = UserId,
            Sport = req.Sport,
            PitchId = IsLegacyPitchId(pitch.Id) ? null : pitch.Id,
            PitchSize = pitchSize,
            Date = date,
            StartTime = req.StartTime,
            Duration = req.Duration,
            Amount = totalAmount,
            TotalAmount = totalAmount,
            DepositAmount = depositAmount,
            SystemFeePercentage = platformFee,
            SystemFee = systemFee,
            OwnerAmount = ownerAmount,
            PaymentMethod = req.PaymentMethod,
            Notes = req.Notes,
            Status = "pending_payment",
            DepositPaid = false,
            RecurringGroupId = group.Id,
        }).ToList();

        _db.Bookings.AddRange(newBookings);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        var ids = newBookings.Select(b => b.Id).ToList();
        var reloaded = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .AsSplitQuery()
            .Where(b => ids.Contains(b.Id))
            .OrderBy(b => b.Date)
            .ToListAsync();

        return Ok(new ApiResponse<RecurringBookingResponse>
        {
            Data = new RecurringBookingResponse
            {
                GroupId = group.Id,
                Created = reloaded.Select(b => ToDto(b)).ToList(),
                SkippedDates = conflictDates.Select(d => d.ToString("yyyy-MM-dd")).ToList(),
                RequestedCount = occurrences.Count,
            },
            Message = $"Created {reloaded.Count} of {occurrences.Count} sessions"
        });
    }

    // PATCH /api/v1/bookings/recurring/{groupId}/cancel — cancel an entire series
    [HttpPatch("recurring/{groupId}/cancel")]
    public async Task<IActionResult> CancelSeries(string groupId)
    {
        var group = await _db.RecurringBookingGroups
            .Include(g => g.Venue)
            .FirstOrDefaultAsync(g => g.Id == groupId);
        if (group == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Series not found" });

        if (UserRole == "player" && group.PlayerId != UserId)
            return Forbid();
        if (UserRole == "venue_owner" && group.Venue.OwnerId != UserId)
            return Forbid();

        var today = DateTime.UtcNow.Date;
        var active = new[] { "pending", "pending_payment", "pending_review", "confirmed" };

        var toCancel = await _db.Bookings
            .Where(b => b.RecurringGroupId == groupId
                && b.Date >= today
                && active.Contains(b.Status))
            .ToListAsync();

        foreach (var b in toCancel) b.Status = "cancelled";
        group.Status = "cancelled";
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Data = new { cancelledCount = toCancel.Count, groupId = group.Id },
            Message = $"Cancelled {toCancel.Count} upcoming session(s)"
        });
    }

    private BookingResponse ToDto(Booking b, bool includeFullProof = false)
    {
        var resolvedPitchId = b.PitchId;
        if (string.IsNullOrEmpty(resolvedPitchId) && b.Venue != null)
        {
            // Legacy row: project to the implicit single pitch for that sport so
            // clients always see a stable `pitchId` and can filter by it.
            var first = PitchSizes.ResolvedPitches(b.Venue)
                .FirstOrDefault(p => string.Equals(p.Sport, b.Sport, StringComparison.OrdinalIgnoreCase));
            resolvedPitchId = first?.Id;
        }

        var dto = new BookingResponse
        {
            Id = b.Id,
            Venue = new VenueRef
            {
                Id = b.Venue.Id,
                Name = b.Venue.Name,
                City = b.Venue.City,
                Images = b.Venue.Images?.Select(x => UploadUrlHelper.Normalize(x, _uploadsBaseUrl)).ToList()!,
                CliqAlias = b.Venue.CliqAlias
            },
            Player = new PlayerRef { Id = b.Player.Id, Name = b.Player.Name },
            Sport = b.Sport,
            PitchId = resolvedPitchId,
            Date = b.Date.ToString("yyyy-MM-dd"),
            StartTime = b.StartTime,
            Duration = b.Duration,
            Amount = b.Amount,
            TotalAmount = b.TotalAmount,
            DepositAmount = b.DepositAmount,
            DepositPaid = b.DepositPaid,
            AmountPaid = b.AmountPaid,
            PaymentMethod = b.PaymentMethod,
            Notes = b.Notes,
            PaymentProof = includeFullProof ? UploadUrlHelper.Normalize(b.PaymentProof, _uploadsBaseUrl) : (b.PaymentProof != null ? "(uploaded)" : null),
            PaymentProofStatus = b.PaymentProofStatus,
            PaymentProofNote = b.PaymentProofNote,
            RecurringGroupId = b.RecurringGroupId,
            Status = b.Status,
            PitchSize = b.PitchSize ?? b.Venue.ParentSize,
            CreatedAt = b.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };

        // Revenue split — admin sees all, owner sees only their cut
        if (UserRole == "super_admin")
        {
            dto.SystemFee = b.SystemFee;
            dto.OwnerAmount = b.OwnerAmount;
            dto.SystemFeePercentage = b.SystemFeePercentage;
        }
        else if (UserRole == "venue_owner")
        {
            dto.OwnerAmount = b.OwnerAmount;
        }

        return dto;
    }

    /// <summary>Result of pitch resolution for an incoming booking request.</summary>
    private sealed class PitchResolution
    {
        public PitchDto? Pitch { get; init; }
        public string? Error { get; init; }
        public int Status { get; init; } = 400;
    }

    /// <summary>
    /// Pick the pitch this booking will live on. Rules:
    /// - Caller supplied <c>pitchId</c> → that pitch must exist and its sport must match.
    /// - Caller omitted it → OK when exactly one pitch on the venue offers the sport;
    ///   otherwise PITCH_REQUIRED so the client knows to prompt.
    /// </summary>
    private static PitchResolution ResolvePitchForBooking(Venue venue, string? pitchId, string sport)
    {
        var pitches = PitchSizes.ResolvedPitches(venue);
        if (!string.IsNullOrEmpty(pitchId))
        {
            var byId = pitches.FirstOrDefault(p => p.Id == pitchId);
            if (byId == null)
                return new PitchResolution { Status = 404, Error = "PITCH_NOT_FOUND" };
            if (!string.Equals(byId.Sport, sport, StringComparison.OrdinalIgnoreCase))
                return new PitchResolution { Error = $"Pitch '{byId.Name}' is for {byId.Sport}, not {sport}." };
            return new PitchResolution { Pitch = byId };
        }

        var matchingSport = pitches
            .Where(p => string.Equals(p.Sport, sport, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matchingSport.Count == 1)
            return new PitchResolution { Pitch = matchingSport[0] };
        if (matchingSport.Count == 0)
            return new PitchResolution { Error = $"Venue has no pitch for {sport}." };
        return new PitchResolution { Error = "PITCH_REQUIRED" };
    }

    /// <summary>Does this booking belong to this pitch? Mirrors VenuesController.MatchesPitch.</summary>
    private static bool BookingOnPitch(Booking b, Venue v, PitchDto pitch)
    {
        if (!string.IsNullOrEmpty(b.PitchId))
            return b.PitchId == pitch.Id;
        var firstOfSport = PitchSizes.ResolvedPitches(v)
            .FirstOrDefault(p => string.Equals(p.Sport, b.Sport, StringComparison.OrdinalIgnoreCase));
        return firstOfSport != null && firstOfSport.Id == pitch.Id;
    }

    /// <summary>
    /// Permanent bookings carry an explicit pitch_id when the venue has multiple
    /// pitches. When pitch_id is null we treat the permanent as belonging to the
    /// only resolved pitch (legacy single-pitch venues).
    /// </summary>
    private static bool PermanentOnPitch(PermanentBooking p, Venue v, PitchDto pitch)
    {
        if (!string.IsNullOrEmpty(p.PitchId))
            return p.PitchId == pitch.Id;
        var resolved = PitchSizes.ResolvedPitches(v);
        return resolved.Count == 1 && resolved[0].Id == pitch.Id;
    }

    /// <summary>Legacy synthesised pitch ids start with "legacy-" and must not hit the DB.</summary>
    private static bool IsLegacyPitchId(string? id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith("legacy-", StringComparison.Ordinal);
}
