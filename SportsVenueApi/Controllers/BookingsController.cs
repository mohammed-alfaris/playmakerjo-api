using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Bookings;
using SportsVenueApi.Constants;
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
    private readonly ILogger<BookingsController> _logger;

    public BookingsController(AppDbContext db, NotificationService notifications, ILogger<BookingsController> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
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
                Message = $"Duration must be between {venue.MinBookingDuration} and {venue.MaxBookingDuration} hours"
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
                    var endTimeSpan = startTimeSpan + TimeSpan.FromHours(req.Duration);
                    if (startTimeSpan < openTime || endTimeSpan > closeTime)
                        return BadRequest(new ApiResponse<object>
                        {
                            Success = false,
                            Message = $"Booking must be within operating hours ({dayHours["open"]} - {dayHours["close"]})"
                        });
                }
            }
        }

        // Check for slot conflicts
        var endTime = startTimeSpan + TimeSpan.FromHours(req.Duration);
        var existingBookings = await _db.Bookings
            .Where(b => b.VenueId == req.VenueId
                && b.Date.Date == bookingDate.Date
                && b.Status != "cancelled"
                && b.StartTime != null)
            .ToListAsync();

        foreach (var existing in existingBookings)
        {
            if (TimeSpan.TryParse(existing.StartTime, out var existingStart))
            {
                var existingEnd = existingStart + TimeSpan.FromHours(existing.Duration);
                // Check overlap: new start < existing end AND new end > existing start
                if (startTimeSpan < existingEnd && endTime > existingStart)
                {
                    return Conflict(new ApiResponse<object>
                    {
                        Success = false,
                        Message = $"Time slot conflicts with an existing booking ({existing.StartTime} - {existingEnd:hh\\:mm})"
                    });
                }
            }
        }

        // Reject card payments (coming soon)
        if (req.PaymentMethod == "stripe")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Card payments coming soon. Please use CliQ." });

        // Calculate amounts + revenue split
        var totalAmount = venue.PricePerHour * req.Duration;
        var depositAmount = totalAmount * (venue.DepositPercentage / 100.0);
        var systemFee = totalAmount * (PlatformConstants.SystemFeePercentage / 100.0);
        var ownerAmount = totalAmount - systemFee;

        var booking = new Booking
        {
            VenueId = req.VenueId,
            PlayerId = UserId,
            Sport = req.Sport,
            Date = bookingDate,
            StartTime = req.StartTime,
            Duration = req.Duration,
            Amount = totalAmount,
            TotalAmount = totalAmount,
            DepositAmount = depositAmount,
            SystemFeePercentage = PlatformConstants.SystemFeePercentage,
            SystemFee = systemFee,
            OwnerAmount = ownerAmount,
            PaymentMethod = req.PaymentMethod,
            Notes = req.Notes,
            Status = "pending_payment",
            DepositPaid = false,
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
                Message = $"Duration must be between {venue.MinBookingDuration} and {venue.MaxBookingDuration} hours"
            });

        var recurType = (req.RecurrenceType ?? "weekly").ToLower();
        if (recurType != "weekly" && recurType != "biweekly")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "recurrenceType must be 'weekly' or 'biweekly'" });

        var stepDays = recurType == "biweekly" ? 14 : 7;
        var policy = (req.ConflictPolicy ?? "skip").ToLower();

        // Compute occurrence dates
        var occurrences = new List<DateTime>();
        for (var d = startDate; d <= endDate; d = d.AddDays(stepDays))
            occurrences.Add(d);

        if (occurrences.Count == 0)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "No occurrences in the selected range" });

        // Validate operating hours for that day-of-week (same every week)
        var endTime = startTimeSpan + TimeSpan.FromHours(req.Duration);
        var dayName = startDate.DayOfWeek.ToString().ToLower()[..3];
        var operatingHours = venue.OperatingHours;
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

        // Pre-load conflicting bookings in the date range
        var existing = await _db.Bookings
            .Where(b => b.VenueId == req.VenueId
                && b.Date >= startDate
                && b.Date <= endDate
                && b.Status != "cancelled"
                && b.StartTime != null)
            .ToListAsync();

        var conflictDates = new List<DateTime>();
        var validDates = new List<DateTime>();
        foreach (var date in occurrences)
        {
            bool conflict = false;
            foreach (var ex in existing.Where(e => e.Date.Date == date.Date))
            {
                if (TimeSpan.TryParse(ex.StartTime, out var exStart))
                {
                    var exEnd = exStart + TimeSpan.FromHours(ex.Duration);
                    if (startTimeSpan < exEnd && endTime > exStart)
                    {
                        conflict = true;
                        break;
                    }
                }
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

        var totalAmount = venue.PricePerHour * req.Duration;
        var depositAmount = totalAmount * (venue.DepositPercentage / 100.0);
        var systemFee = totalAmount * (PlatformConstants.SystemFeePercentage / 100.0);
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
            Date = date,
            StartTime = req.StartTime,
            Duration = req.Duration,
            Amount = totalAmount,
            TotalAmount = totalAmount,
            DepositAmount = depositAmount,
            SystemFeePercentage = PlatformConstants.SystemFeePercentage,
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
        var dto = new BookingResponse
        {
            Id = b.Id,
            Venue = new VenueRef
            {
                Id = b.Venue.Id,
                Name = b.Venue.Name,
                City = b.Venue.City,
                Images = b.Venue.Images,
                CliqAlias = b.Venue.CliqAlias
            },
            Player = new PlayerRef { Id = b.Player.Id, Name = b.Player.Name },
            Sport = b.Sport,
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
            PaymentProof = includeFullProof ? b.PaymentProof : (b.PaymentProof != null ? "(uploaded)" : null),
            PaymentProofStatus = b.PaymentProofStatus,
            PaymentProofNote = b.PaymentProofNote,
            RecurringGroupId = b.RecurringGroupId,
            Status = b.Status,
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
}
