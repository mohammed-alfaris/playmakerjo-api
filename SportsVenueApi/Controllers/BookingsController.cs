using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    private readonly ExpiryPolicy _expiry;
    private readonly string _uploadsBaseUrl;

    public BookingsController(
        AppDbContext db,
        NotificationService notifications,
        SettingsService settings,
        ILogger<BookingsController> logger,
        ExpiryPolicy expiry,
        IConfiguration config)
    {
        _db = db;
        _notifications = notifications;
        _settings = settings;
        _logger = logger;
        _expiry = expiry;
        _uploadsBaseUrl = config["Uploads:BaseUrl"]?.TrimEnd('/') ?? "";
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    /// <summary>The venue_owner a staff caller works for. Null for every other role.</summary>
    private string? StaffOwnerId => User.FindFirstValue("owner_id");

    /// <summary>"read" | "write" for a staff caller. Null for every other role.</summary>
    private string? StaffPermissions => User.FindFirstValue("permissions");

    /// <summary>The owner whose data this caller belongs to — themselves, or their boss.</summary>
    private string? EffectiveOwnerId => UserRole switch
    {
        "venue_owner" => UserId,
        "venue_staff" => StaffOwnerId,
        _ => null,
    };

    /// <summary>
    /// May this caller act on the booking from the venue's side — i.e. confirm, complete,
    /// mark no-show, or review a payment proof? Mirrors <see cref="Helpers.VenueAccess"/>.
    ///
    /// Every guard in this controller used to be written as a deny-list
    /// (<c>if (UserRole == "venue_owner" &amp;&amp; ...) return Forbid();</c>), which named the
    /// roles to block and let everything else through. A venue_staff token — a real role
    /// this product creates — matched neither branch and so passed all of them, gaining
    /// unrestricted control over every booking on the platform. Allow-listing instead
    /// means an unrecognised or future role is denied rather than trusted.
    /// Requires <c>booking.Venue</c> to be loaded.
    /// </summary>
    private bool CanManageBooking(Booking booking) =>
        VenueAccess.CanWrite(booking.Venue, UserId, UserRole, StaffOwnerId, StaffPermissions);

    /// <summary>
    /// May this caller see or cancel this booking — venue-side, or the player who made it?
    /// Read-only staff land here but not in <see cref="CanManageBooking"/>.
    /// </summary>
    private bool CanAccessBooking(Booking booking) =>
        VenueAccess.CanView(booking.Venue, UserId, UserRole, StaffOwnerId)
        || (UserRole == "player" && booking.PlayerId == UserId);

    /// <summary>
    /// Find-or-create the customer for a walk-in, keyed on (owner, normalised phone).
    ///
    /// Returns null — meaning "no customer recorded" — when no usable phone was given. That
    /// is deliberate: the phone is the identity, and a nameless row with no number would be
    /// an un-deduplicable ghost that pollutes the book forever. A booking without a phone is
    /// still a perfectly good booking.
    ///
    /// A stored name is never overwritten by a later booking: the owner may have corrected
    /// the spelling in the customer sheet, and a hurried re-type at the counter must not
    /// undo that. An empty stored name is filled in when one finally arrives.
    /// </summary>
    private async Task<string?> ResolveCustomerAsync(string ownerId, string? rawPhone, string? rawName)
    {
        var phone = PhoneNormalizer.ToE164Jo(rawPhone);
        if (phone == null) return null;

        var name = rawName?.Trim() ?? "";
        if (name.Length > 120) name = name[..120];

        var existing = await _db.Customers
            .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.Phone == phone);

        if (existing != null)
        {
            if (string.IsNullOrWhiteSpace(existing.Name) && name.Length > 0)
            {
                existing.Name = name;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
            return existing.Id;
        }

        var customer = new Customer
        {
            OwnerId = ownerId,
            Phone = phone,
            Name = name,
            CreatedByUserId = UserId,
        };
        _db.Customers.Add(customer);

        try
        {
            await _db.SaveChangesAsync();
            return customer.Id;
        }
        catch (DbUpdateException)
        {
            // Two clerks taking the same person's booking at the same moment both miss the
            // read and both insert; the unique index rejects one. Re-read rather than fail
            // the booking — the customer exists either way, which is all we needed.
            _db.Entry(customer).State = EntityState.Detached;
            var raced = await _db.Customers
                .FirstOrDefaultAsync(c => c.OwnerId == ownerId && c.Phone == phone);
            return raced?.Id;
        }
    }

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
        // Deny-by-default scoping. This previously pinned ownerId only for venue_owner
        // and then applied the filter only when ownerId was non-empty — so a player,
        // a venue_staff account, or any unrecognised role received EVERY booking on the
        // platform, including other customers' names and every venue's amounts. Players
        // have /bookings/my for their own list; this back-office list is not for them.
        var baseQuery = _db.Bookings.AsQueryable();

        if (UserRole == "super_admin")
        {
            // Admins may narrow to one owner; omitting owner_id means platform-wide.
            if (!string.IsNullOrEmpty(owner_id))
                baseQuery = baseQuery.Where(b => b.Venue.OwnerId == owner_id);
        }
        else if (EffectiveOwnerId is { Length: > 0 } scopedOwnerId)
        {
            // Owners see their own; staff see their employer's. owner_id from the query
            // string is ignored in both cases — scope comes from the token. An unlinked
            // staff account has no EffectiveOwnerId and falls through to Forbid.
            baseQuery = baseQuery.Where(b => b.Venue.OwnerId == scopedOwnerId);
        }
        else
        {
            return Forbid();
        }

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
            .Include(b => b.Customer)
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
            .Include(b => b.Customer)
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
            .Include(b => b.Customer)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        // Players see their own bookings, venue side sees bookings for their venues,
        // admins see all. Everyone else is denied.
        if (!CanAccessBooking(booking))
            return Forbid();

        return Ok(new ApiResponse<BookingResponse> { Data = ToDto(booking, includeFullProof: true) });
    }

    // POST /api/v1/bookings — create a new booking
    [HttpPost]
    [EnableRateLimiting("booking-create")]
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

        if (bookingDate.Date < PlatformConstants.JordanToday())
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

        // Resolve which pitch this booking lives on.
        var pitchResolution = ResolvePitchForBooking(venue, req.PitchId, req.Sport);
        if (pitchResolution.Error != null)
            return pitchResolution.Status == 404
                ? NotFound(new ApiResponse<object> { Success = false, Message = pitchResolution.Error })
                : BadRequest(new ApiResponse<object> { Success = false, Message = pitchResolution.Error });
        var pitch = pitchResolution.Pitch!;

        // Operating-hours check. Deliberately after pitch resolution so a pitch with its
        // own hours is honoured exactly as the availability view does.
        //
        // This used to build its own key as DayOfWeek.ToString().ToLower()[..3] ("mon")
        // and look up ONLY that. The dashboard writes full day names ("monday"), so the
        // lookup missed, the whole block was skipped, and operating hours were never
        // enforced on booking creation for any venue — a player could book 03:00 on a
        // closed day. It also never read the "closed" flag. AvailabilityHelper accepts
        // both key styles and honours "closed", and is the same code the availability
        // view uses, so the two can no longer disagree.
        var dayName = bookingDate.DayOfWeek.ToString().ToLower();

        var hoursCheck = AvailabilityHelper.CheckSlotAgainstHours(
            venue, pitch, dayName, startTimeSpan, req.Duration);
        switch (hoursCheck.Verdict)
        {
            case SlotHoursVerdict.Misconfigured:
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "This venue's operating hours are misconfigured. Please contact the venue."
                });
            case SlotHoursVerdict.OutsideHours:
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = $"Booking must be within operating hours ({hoursCheck.Open} - {hoursCheck.Close})"
                });
            case SlotHoursVerdict.Closed:
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "The venue is closed on this day."
                });
        }

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

        // ── Everything from here to the commit is one serialized critical section ──
        //
        // Availability was a read-then-write with nothing in between: two people booking the
        // same 21:00 slot both read "free", both passed the capacity check, and both
        // committed. On a CliQ platform each of them then transfers a real deposit to the
        // owner's alias for a pitch only one of them can have.
        //
        // A unique index cannot fix this. Subdividable pitches legitimately allow several
        // overlapping rows on the same (venue, pitch, date, start_time) — an 11-aside field
        // holds four 5-aside games at once — so the rule being enforced is a SUM against a
        // capacity budget, not uniqueness. There is no column to make unique.
        //
        // So we serialize per venue: take a row lock on the venue, then read, decide and
        // insert inside it. Concurrent attempts for the same venue queue behind the lock and
        // each sees the previous one's booking. Different venues never block each other, and
        // at this scale (a busy pitch takes ~7 bookings a day) the contention is nil.
        await using var tx = await _db.Database.BeginTransactionAsync();

        // FOR UPDATE on the venue row is the lock. AsNoTracking because `venue` is already
        // tracked from the earlier Find; this query exists for its lock, not its result.
        //
        // Verified by removing this block and re-running ConcurrentBookingTests: three of
        // the five fail without it, including five simultaneous requests all being accepted
        // for the same slot. A test that passes with and without the fix proves nothing, so
        // that check is worth repeating if this code is ever refactored.
        _ = await _db.Venues
            .FromSql($"SELECT * FROM venues WHERE id = {req.VenueId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync();

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
            // Admin, the venue's owner, or that owner's staff with "write". Read-only staff
            // and everyone else are refused. This is the counter clerk's entire job, so it
            // is the one place staff access genuinely has to work.
            if (!VenueAccess.CanWrite(venue, UserId, UserRole, StaffOwnerId, StaffPermissions))
                return Forbid();
        }

        // Who is this booking for, in THIS owner's book?
        //
        // Both channels resolve to the same customer record, keyed on phone. That is what
        // makes the history complete: a regular who phones in June and switches to the app
        // in July is one person with six bookings, not two people with three each — and
        // does not wrongly appear in the "stopped coming" list.
        //
        // Resolution happens after the venue is known, so the customer always lands in the
        // correct owner's book and never in a competitor's. Nothing here crosses owners:
        // two owners each keep their own separate record of the same phone.
        string? customerId;
        if (isManual)
        {
            customerId = await ResolveCustomerAsync(venue.OwnerId, req.CustomerPhone, req.CustomerName);
        }
        else
        {
            // App booking: the player is the customer, from this venue's point of view.
            // Their phone is optional on the User record, so this may resolve to nothing.
            var bookingPlayer = await _db.Users.FindAsync(UserId);
            customerId = await ResolveCustomerAsync(venue.OwnerId, bookingPlayer?.Phone, bookingPlayer?.Name);
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
            CustomerId = customerId,
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
            // The slot is held either way — a phone booking blocks the pitch whether or not
            // the money has arrived yet. What changes is whether we claim to have been paid.
            Status = isManual ? "confirmed" : "pending_payment",
            IsManual = isManual,
            // Deliberately left unpaid here and raised below through PaymentLedger.Settle,
            // so the amount and the row that evidences it are produced by the same call.
            DepositPaid = false,
            AmountPaid = 0,
            // ARM THE ROW — and only this row, on only this path.
            //
            // A counter booking is never armed, because an owner holding a slot for someone
            // he knows has made a decision the system has no business overruling at 3am.
            // Null here means immune forever, not "due later"; see Booking.PaymentDeadlineAt.
            PaymentDeadlineAt = isManual
                ? null
                : PaymentDeadline.Compute(DateTime.UtcNow, bookingDate, req.StartTime, _expiry),
        };

        _db.Bookings.Add(booking);

        if (isManual && req.CustomerPaid)
        {
            var receipt = PaymentLedger.Settle(
                booking, totalAmount, "full", UserId, "Paid at the venue");
            if (receipt != null) _db.Payments.Add(receipt);
        }

        // One SaveChanges, inside the venue lock: the booking and its receipt commit
        // together or neither does. Money recorded against a booking that failed to
        // insert would be worse than no ledger at all.
        await _db.SaveChangesAsync();

        // Releases the venue lock. The next queued attempt for this venue now reads a
        // picture that includes this booking.
        await tx.CommitAsync();

        // Reload with includes
        booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .Include(b => b.Customer)
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
            .Include(b => b.Customer)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        // Players cancel their own, venue side cancels bookings for their venues,
        // admins cancel any. Everyone else is denied.
        if (!CanAccessBooking(booking))
            return Forbid();

        // Can only cancel pending/confirmed bookings
        if (booking.Status == "cancelled")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Booking is already cancelled" });

        if (booking.Status == "completed")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Cannot cancel a completed booking" });

        if (booking.Status == "no_show")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Cannot cancel a no-show booking" });

        booking.Status = "cancelled";
        // Disarm. A human cancelled this one — leaving a live deadline on a terminal row
        // would let the sweep stamp AutoCancelledAt on it later and rewrite whose decision
        // it was.
        booking.PaymentDeadlineAt = null;
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
            .Include(b => b.Customer)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (!CanManageBooking(booking))
            return Forbid();

        if (booking.Status != "confirmed")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Only confirmed bookings can be marked as completed" });

        // Completing a booking is ONE act at the counter, not two: the customer played and he
        // paid. So any outstanding balance is collected here rather than being a precondition
        // the owner has to satisfy with a separate tap first.
        //
        // This is what makes the "owed" figure work. Owed counts attended-but-underpaid
        // manual bookings, and completed always counts as attended — so if completing could
        // leave a balance behind, a real debt would silently stop being visible the moment the
        // owner marked the visit done. The customer who played and did NOT pay is represented
        // by simply not completing the booking: it stays confirmed, the date passes, and the
        // derived "attended" rule picks it up as owed. Absence of this tap IS the debt.
        //
        // For collecting money WITHOUT completing — an upcoming slot paid in advance, or a
        // pending_payment booking settled at the counter — use mark-paid instead. This does
        // not replace it; it just means the common case is one tap rather than two.
        var remaining = Math.Round(booking.TotalAmount - booking.AmountPaid, 3);
        var receipt = remaining > PaymentLedger.Epsilon
            ? PaymentLedger.Settle(
                booking, booking.TotalAmount, "balance", UserId, "Collected on completion")
            : null;
        if (receipt != null) _db.Payments.Add(receipt);

        booking.Status = "completed";
        // Disarm, for the same reason Cancel does: a live deadline on a terminal row is a
        // trap for the expiry sweep. It only looks at pending_payment today, so this is
        // defence in depth rather than a live bug — but "completed" is exactly as terminal
        // as "cancelled", and the two should not disagree about their own invariant.
        booking.PaymentDeadlineAt = null;
        // One SaveChanges: the status and its matching ledger row commit together or not at
        // all. Splitting them is how a booking ends up completed with the money unrecorded.
        await _db.SaveChangesAsync();

        try { await _notifications.NotifyBookingCompleted(booking); }
        catch (Exception ex) { _logger.LogWarning(ex, "Notification failed"); }

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = receipt != null
                ? $"Booking completed — {receipt.Amount:0.###} JOD collected"
                : "Booking marked as completed"
        });
    }

    // PATCH /api/v1/bookings/{id}/no-show — mark a confirmed booking as no-show
    [HttpPatch("{id}/no-show")]
    public async Task<IActionResult> NoShow(string id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .Include(b => b.Customer)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (!CanManageBooking(booking))
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

    /// <summary>
    /// PATCH /api/v1/bookings/{id}/mark-paid — the money for a manual booking has arrived.
    ///
    /// The counterpart to <c>customerPaid: false</c> at creation: a slot booked by phone on
    /// Sunday and paid on arrival Tuesday. Without this the owner would have no way to
    /// settle it and the booking would sit as owing forever.
    /// </summary>
    [HttpPatch("{id}/mark-paid")]
    public async Task<IActionResult> MarkPaid(string id)
    {
        var booking = await _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .Include(b => b.Customer)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        if (!CanManageBooking(booking))
            return Forbid();

        if (booking.Status is "cancelled")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Cannot settle a cancelled booking" });

        var receipt = PaymentLedger.Settle(
            booking,
            booking.TotalAmount,
            // A booking that already took a deposit is having its remainder settled; one
            // that never took anything is being paid outright. The ledger should say which.
            booking.AmountPaid <= PaymentLedger.Epsilon ? "full" : "balance",
            UserId,
            "Settled at the venue");

        // Nothing left to collect. Returning 200 rather than an error is deliberate: a
        // double-tap on a slow connection is not a mistake worth shouting about, and the
        // end state the owner asked for is already true. What matters is that no second
        // row is written, so the day's takings are not counted twice.
        if (receipt == null)
            return Ok(new ApiResponse<BookingResponse> { Data = ToDto(booking), Message = "Already settled" });

        // The money is in. Whatever hold was on this slot, it is not an unpaid one any more.
        booking.PaymentDeadlineAt = null;
        _db.Payments.Add(receipt);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<BookingResponse> { Data = ToDto(booking), Message = "Marked as paid" });
    }

    /// <summary>
    /// GET /api/v1/bookings/attendance-pending — slots that have already happened but are
    /// still sitting at "confirmed", i.e. nobody has said whether the customer turned up.
    ///
    /// This exists because nothing in the system can observe attendance. There is no gate,
    /// no check-in, no sensor: the only source of truth is a human saying so. Left to
    /// itself nobody ever taps "no-show" — there is no reason to, when everything went
    /// fine — so the no-show count stays 0 forever and the warning that is supposed to
    /// protect the owner never fires.
    ///
    /// The UI asks about the EXCEPTION, not the rule: "did anyone not turn up?", with
    /// everyone assumed present. A normal day costs zero taps.
    /// </summary>
    [HttpGet("attendance-pending")]
    public async Task<IActionResult> AttendancePending([FromQuery] int days = 7, [FromQuery] int limit = 50)
    {
        if (days is < 1 or > 30) days = 7;
        if (limit is < 1 or > 200) limit = 50;

        var today = PlatformConstants.JordanToday();
        var since = today.AddDays(-days);

        var query = _db.Bookings
            .Include(b => b.Venue)
            .Include(b => b.Player)
            .Include(b => b.Customer)
            .AsSplitQuery()
            // Strictly before today: a slot later this afternoon has not happened yet, and
            // asking about it would train the owner to answer before he knows.
            .Where(b => b.Status == "confirmed" && b.Date.Date < today && b.Date.Date >= since);

        if (UserRole == "super_admin")
        {
            // Nothing extra — admins review nothing in practice, but the shape stays uniform.
        }
        else if (EffectiveOwnerId is { Length: > 0 } ownerId)
        {
            query = query.Where(b => b.Venue.OwnerId == ownerId);
        }
        else
        {
            return Forbid();
        }

        var pending = await query
            .OrderBy(b => b.Date)
            .ThenBy(b => b.StartTime)
            .Take(limit)
            .ToListAsync();

        return Ok(new ApiResponse<List<BookingResponse>>
        {
            Data = pending.Select(b => ToDto(b)).ToList()
        });
    }

    /// <summary>
    /// POST /api/v1/bookings/attendance-confirm — "everyone else turned up".
    ///
    /// Marks the given past bookings completed in one call. Deliberately separate from the
    /// per-booking no-show tap: the owner marks the handful who did NOT come, then confirms
    /// the rest in a single action.
    ///
    /// Skipping this entirely is safe. Attendance is DERIVED as
    /// <c>completed OR (confirmed AND in the past AND not no_show)</c>, so an owner who
    /// ignores the prompt still gets correct attendance counts — he just leaves the rows at
    /// "confirmed". Confirming is what turns an assumption into a recorded fact, which is
    /// also what makes "completed" trustworthy enough to hang revenue off later.
    /// </summary>
    [HttpPost("attendance-confirm")]
    public async Task<IActionResult> AttendanceConfirm([FromBody] AttendanceConfirmRequest req)
    {
        if (req.BookingIds is not { Count: > 0 })
            return BadRequest(new ApiResponse<object> { Success = false, Message = "bookingIds is required" });

        if (req.BookingIds.Count > 200)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Too many bookings in one call" });

        var today = PlatformConstants.JordanToday();

        var bookings = await _db.Bookings
            .Include(b => b.Venue)
            .Where(b => req.BookingIds.Contains(b.Id))
            .ToListAsync();

        var updated = 0;
        foreach (var booking in bookings)
        {
            // Re-check every row: ids come from the client, and a stale tab could carry
            // bookings from a venue the caller no longer manages.
            if (!CanManageBooking(booking)) continue;
            if (booking.Status != "confirmed") continue;
            if (booking.Date.Date >= today) continue;

            booking.Status = "completed";
            updated++;
        }

        if (updated > 0) await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object>
        {
            Data = new { confirmed = updated },
            Message = $"{updated} booking(s) marked as attended"
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
            .Include(b => b.Customer)
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
            .Include(b => b.Customer)
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

        // Proof is a base64 image we persist and later serve back to the owner —
        // validate it's real base64, size-capped, and an actual image (not arbitrary
        // bytes) before storing.
        var proofData = req.PaymentProof;
        var commaIdx = proofData.IndexOf(',');
        if (proofData.StartsWith("data:") && commaIdx >= 0)
            proofData = proofData[(commaIdx + 1)..];
        byte[] proofBytes;
        try { proofBytes = Convert.FromBase64String(proofData); }
        catch (FormatException) { return BadRequest(new ApiResponse<object> { Success = false, Message = "Payment proof is not valid base64" }); }
        if (proofBytes.Length > 5 * 1024 * 1024)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Payment proof image is too large (max 5MB)" });
        if (!ImageBytes.HasAllowedSignature(proofBytes))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Payment proof is not a valid image" });

        booking.PaymentProof = req.PaymentProof;
        booking.PaymentProofStatus = "pending_review";
        booking.Status = "pending_review";
        // DISARM. The customer has done their part and the CliQ transfer may already be
        // real; from here the booking is waiting on the OWNER to look at a screenshot.
        // Expiring it would penalise the customer for the venue's inaction.
        booking.PaymentDeadlineAt = null;
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
            .Include(b => b.Customer)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        // Only the venue's own side may review a payment proof — approving one marks
        // money as received, so this must never fall through to an unnamed role.
        if (!CanManageBooking(booking))
            return Forbid();

        if (booking.PaymentProofStatus != "pending_review")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "No proof pending review" });

        if (req.Approved)
        {
            booking.PaymentProofStatus = "approved";
            booking.Status = "confirmed";
            booking.PaymentProofNote = null;
            booking.PaymentDeadlineAt = null;

            // Approving a screenshot is the moment a human asserts the transfer is real, so
            // it is the moment worth recording — with the reviewer's id attached, because
            // approval is a judgement call someone has to answer for.
            //
            // Settle only ever raises the paid amount. The line it replaces assigned
            // DepositAmount outright, which would silently REDUCE the recorded total on a
            // booking that had already paid more.
            var receipt = PaymentLedger.Settle(
                booking, booking.DepositAmount, "deposit", UserId, "CliQ proof approved", "cliq");
            if (receipt != null) _db.Payments.Add(receipt);
        }
        else
        {
            booking.PaymentProofStatus = "rejected";
            booking.PaymentProofNote = req.Note ?? "Payment proof was rejected";
            booking.Status = "pending_payment";  // back to pending so player can re-upload
            booking.PaymentProof = null;  // clear the rejected proof

            // RE-ARM FROM NOW, not from creation.
            //
            // This is the whole reason the deadline is a stored instant rather than an age
            // computed from CreatedAt at sweep time. A booking bounced back for a blurry
            // screenshot re-enters pending_payment with CreatedAt untouched, and the table
            // has no updated_at — so "how long has it been waiting" is not answerable from
            // the row. Re-stamping makes it answerable, and gives the customer a fresh,
            // full window to re-upload instead of a deadline that expired hours ago.
            booking.PaymentDeadlineAt = booking.IsManual
                ? null
                : PaymentDeadline.Compute(DateTime.UtcNow, booking.Date, booking.StartTime, _expiry);
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
            .Include(b => b.Customer)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id);

        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });

        // Venue side only. The old guard permitted a player acting on their OWN booking,
        // which was a complete payment bypass: create a pending_payment booking, call
        // this endpoint, and walk away with status=confirmed and depositPaid=true having
        // transferred nothing. A player's route to confirmed is upload-proof, then the
        // venue approving it in review-proof.
        if (!CanManageBooking(booking))
            return Forbid();

        if (booking.Status != "pending" && booking.Status != "pending_payment")
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Cannot confirm a booking with status '{booking.Status}'" });

        booking.Status = "confirmed";
        booking.PaymentDeadlineAt = null;

        // Confirming a booking that was waiting on money IS the owner stating the deposit
        // arrived — that is the only thing this flag has ever meant. The line this replaces
        // set DepositPaid = true and left AmountPaid at zero, so the row claimed to be paid
        // and to have received nothing at the same time, and the customer's balance owed
        // was overstated by the deposit on every booking confirmed this way.
        var receipt = PaymentLedger.Settle(
            booking, booking.DepositAmount, "deposit", UserId, "Confirmed by the venue");

        if (receipt != null)
            _db.Payments.Add(receipt);
        else
            // A venue configured with no deposit has nothing to receive at this point, so
            // there is no payment to record — but the flag is still vacuously true.
            booking.DepositPaid = true;

        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<BookingResponse>
        {
            Data = ToDto(booking),
            Message = "Booking confirmed"
        });
    }

    // POST /api/v1/bookings/recurring — create a recurring series
    // Player-created recurring series are intended product behaviour. Owner-managed
    // recurring slots live in PermanentBookingsController, which enforces CanManageVenue.
    [HttpPost("recurring")]
    [EnableRateLimiting("booking-create")]
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

        if (startDate < PlatformConstants.JordanToday())
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
        // Same 3-letter-key bug the one-off path had: this built "sun" while the dashboard
        // writes "sunday", so the lookup missed and hours were never enforced on a recurring
        // series either. AvailabilityHelper accepts both spellings and honours "closed".
        var endTime = startTimeSpan + TimeSpan.FromMinutes(req.Duration);
        var dayName = startDate.DayOfWeek.ToString().ToLower();

        var hoursCheck = AvailabilityHelper.CheckSlotAgainstHours(
            venue, pitch, dayName, startTimeSpan, req.Duration);
        switch (hoursCheck.Verdict)
        {
            case SlotHoursVerdict.Misconfigured:
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "This venue's operating hours are misconfigured. Please contact the venue."
                });
            case SlotHoursVerdict.OutsideHours:
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = $"Booking must be within operating hours ({hoursCheck.Open} - {hoursCheck.Close})"
                });
            case SlotHoursVerdict.Closed:
                return BadRequest(new ApiResponse<object>
                {
                    Success = false,
                    Message = "The venue is closed on this day."
                });
        }

        // Same serialized critical section as the one-off path. This method already opened
        // a transaction, but only AFTER the conflict scan — which left exactly the race the
        // transaction looked like it was preventing. The lock has to be taken before the
        // read, not before the write.
        await using var tx = await _db.Database.BeginTransactionAsync();

        _ = await _db.Venues
            .FromSql($"SELECT * FROM venues WHERE id = {req.VenueId} FOR UPDATE")
            .AsNoTracking()
            .ToListAsync();

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

        // The transaction was opened before the conflict scan above.
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
            .Include(b => b.Customer)
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

        // Allow-list: the series owner's player account, the venue side, or an admin.
        var canCancelSeries =
            UserRole == "super_admin"
            || (UserRole == "venue_owner" && group.Venue.OwnerId == UserId)
            || (UserRole == "player" && group.PlayerId == UserId);
        if (!canCancelSeries)
            return Forbid();

        var today = PlatformConstants.JordanToday();
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

        // Is the caller on the venue's own side of the counter — its owner, that
        // owner's linked staff, or an admin? Two fields below are back-office only,
        // and both used to travel to every caller who could reach a booking.
        // Fails closed: an unloaded Venue navigation yields no back-office access
        // rather than an accidental grant.
        var isBackOffice = b.Venue != null
            && VenueAccess.CanView(b.Venue, UserId, UserRole, StaffOwnerId);

        // The player on THIS booking. Not a role — a relationship to one row.
        //
        // Closing the alias leak went one step too far: it removed the value from
        // everyone outside the back office, including the one person being asked to
        // transfer money. The app rendered its placeholder string where the alias
        // should be, so the booking could never be paid and the expiry sweep
        // eventually cancelled it. A security fix that silently breaks the payment
        // funnel is a worse outage than the leak it closed.
        //
        // Deliberately NOT done by widening VenueAccess.CanView: that helper decides
        // back-office access in a dozen places, and a player must not gain any of
        // them. This grants exactly one venue's alias to someone already holding a
        // booking there — the person who needs it, and no wider.
        // The ROLE check is load-bearing, not decoration. Without it a rival venue_owner
        // books a slot at a competitor's pitch and reads the alias straight off their own
        // booking — a harvest that costs one booking per victim. Restricting to players
        // means an attacker must hold a player account and make a real, payable booking to
        // learn one venue's alias, which is indistinguishable from being a customer.
        var isBookingOwner = UserRole == "player" && b.PlayerId == UserId;

        var dto = new BookingResponse
        {
            Id = b.Id,
            Venue = new VenueRef
            {
                Id = b.Venue.Id,
                Name = b.Venue.Name,
                NameAr = b.Venue.NameAr,
                City = b.Venue.City,
                CityAr = b.Venue.CityAr,
                Images = b.Venue.Images?.Select(x => UploadUrlHelper.Normalize(x, _uploadsBaseUrl)).ToList()!,
                // The alias was stripped from the venue routes and left here, so it
                // stayed harvestable: POST a booking against any venue id, read it
                // off the 201, cancel. Venue ids come from the anonymous public list,
                // so a free account could sweep the whole platform.
                //
                // That sweep is still blocked — but the player who booked now gets it,
                // because they cannot pay without it. Harvesting one alias by making a
                // real booking you must then pay for is not a leak; it is a customer.
                CliqAlias = (isBackOffice || isBookingOwner) ? b.Venue.CliqAlias : null
            },
            Player = new PlayerRef { Id = b.Player.Id, Name = b.Player.Name },
            // The customer book belongs to the venue, not to whoever is holding the
            // slot. A player's profile phone is self-asserted and unverified, so
            // returning the matched customer told them the name behind any number
            // they cared to type. Players learn nothing here they did not supply.
            Customer = isBackOffice && b.Customer != null
                ? new CustomerRef { Id = b.Customer.Id, Name = b.Customer.Name, Phone = b.Customer.Phone }
                : null,
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
            IsManual = b.IsManual,
            PitchSize = b.PitchSize ?? b.Venue.ParentSize,
            PaymentDeadlineAt = b.PaymentDeadlineAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            AutoCancelledAt = b.AutoCancelledAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            CreatedAt = b.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
        };

        // The revenue split is a platform concept and stays on the platform side.
        //
        // The product is sold as the venue's own management system, so an owner is not one
        // tenant of a marketplace taking a cut — he is a subscriber. Showing him a "platform
        // fee" and an "owner payout" tells him someone upstream is skimming his takings and
        // invites the one objection that kills the sale. Staff never see money at all.
        //
        // Note this is stripped at the DTO, not merely hidden in the UI: the fields used to
        // travel to the browser on every booking even when nothing rendered them.
        if (UserRole == "super_admin")
        {
            dto.SystemFee = b.SystemFee;
            dto.OwnerAmount = b.OwnerAmount;
            dto.SystemFeePercentage = b.SystemFeePercentage;
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
