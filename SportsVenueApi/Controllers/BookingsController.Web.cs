using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.DTOs;
using SportsVenueApi.DTOs.Web;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;
using SportsVenueApi.Services;

namespace SportsVenueApi.Controllers;

/// <summary>
/// The venue's public booking link (playmakerjo.com/v/{slug}): a guest with no account books
/// with a name and a phone, either as a request the venue confirms (pay at the venue) or by
/// paying the CliQ deposit now and uploading the transfer. Everything after that goes through
/// the booking's private token.
///
/// It lives in BookingsController so it runs the very same slot checks, venue lock, conflict
/// check and pricing as every other booking — a web booking can never be accepted where a
/// counter or app booking would be refused. It is stored as IsManual (no player account: no
/// player notifications, reminders or commission) with Source = "web".
/// </summary>
public partial class BookingsController
{
    /// <summary>How long a venue has to answer a request before it is released.</summary>
    private static readonly ExpiryPolicy RequestWindow = new(WindowMinutes: 180, SlotBufferMinutes: 30, MinimumMinutes: 10);

    private const int MaxOpenWebBookingsPerCustomer = 3;
    private static readonly string[] OpenStatuses = ["pending", "pending_payment", "pending_review"];

    // GET /api/v1/public/v/{slug}
    [AllowAnonymous]
    [HttpGet("/api/v1/public/v/{slug}")]
    [EnableRateLimiting("web-public")]
    public async Task<IActionResult> WebVenue(string slug)
    {
        var venue = await FindBySlugAsync(slug);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var phone = await _db.Users.Where(u => u.Id == venue.OwnerId).Select(u => u.Phone).FirstOrDefaultAsync();
        return Ok(new ApiResponse<WebVenueResponse>
        {
            Data = new WebVenueResponse
            {
                Id = venue.Id, Slug = venue.Slug ?? venue.Id, Name = venue.Name, NameAr = venue.NameAr,
                City = venue.City, CityAr = venue.CityAr, Address = venue.Address, AddressAr = venue.AddressAr,
                Latitude = venue.Latitude, Longitude = venue.Longitude,
                Images = venue.Images.Select(i => UploadUrlHelper.Normalize(i, _uploadsBaseUrl) ?? i).ToList(),
                Sports = venue.Sports, Pitches = PitchSizes.ResolvedPitches(venue), PricePerHour = venue.PricePerHour,
                MinDuration = venue.MinBookingDuration, MaxDuration = venue.MaxBookingDuration,
                DepositPercentage = venue.DepositPercentage, FreeCancelHours = venue.FreeCancelHours,
                CanPayNow = CanTakeDeposit(venue),
                AcceptingBookings = await AcceptsBookingsAsync(venue),
                Phone = phone,
            },
        });
    }

    // POST /api/v1/public/v/{slug}/requests
    [AllowAnonymous]
    [HttpPost("/api/v1/public/v/{slug}/requests")]
    [EnableRateLimiting("web-request")]
    public async Task<IActionResult> WebBook(string slug, [FromBody] WebBookingRequest req)
    {
        // A bot filled the field people never see. Refuse without saying why.
        if (!string.IsNullOrEmpty(req.Website))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Request refused" });

        var venue = await FindBySlugAsync(slug);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });
        if (!await AcceptsBookingsAsync(venue))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This venue is not taking bookings right now" });
        if (!venue.Sports.Contains(req.Sport, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new ApiResponse<object> { Success = false, Message = $"Venue does not offer {req.Sport}" });
        if (req.PayNow && !CanTakeDeposit(venue))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This venue does not take payment online" });

        var name = (req.Name ?? "").Trim();
        if (name.Length < 2)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Enter your name" });
        if (!PhoneNormalizer.IsJordanianMobile(req.Phone))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Enter a valid Jordanian mobile number" });

        var invalid = PlanSlot(venue, req.Sport, req.Date, req.StartTime, req.Duration, req.PitchId, req.PitchSize, out var plan);
        if (invalid != null) return invalid;

        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockVenueAsync(venue.Id);

        var conflict = await CheckSlotFreeAsync(venue, plan!, excludeBookingId: null);
        if (conflict != null)
            return Conflict(new ApiResponse<object> { Success = false, Message = "That time was just taken. Please pick another." });

        // The guest becomes (or already is) a customer in this venue's own book, by phone —
        // the same record a counter booking or, later, an app booking with that number finds.
        var customerId = await ResolveCustomerAsync(venue.OwnerId, req.Phone, name);
        if (customerId == null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Enter a valid Jordanian mobile number" });

        var open = await _db.Bookings.CountAsync(b => b.CustomerId == customerId && b.Source == Booking.WebSource
            && OpenStatuses.Contains(b.Status));
        if (open >= MaxOpenWebBookingsPerCustomer)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "You already have bookings waiting for this venue. Wait for them to be confirmed first." });

        var total = PriceFor(venue, plan!.Pitch, plan.PitchSize, req.Duration);
        var now = DateTime.UtcNow;
        var booking = new Booking
        {
            VenueId = venue.Id,
            // No player account: like a counter booking, the owner stands in as PlayerId and the
            // person is the customer record.
            PlayerId = venue.OwnerId,
            CustomerId = customerId,
            Sport = req.Sport,
            PitchId = IsLegacyPitchId(plan.Pitch.Id) ? null : plan.Pitch.Id,
            PitchSize = plan.PitchSize,
            Date = plan.Date,
            StartTime = plan.StartTime,
            Duration = req.Duration,
            Amount = total,
            TotalAmount = total,
            DepositAmount = total * (venue.DepositPercentage / 100.0),
            SystemFeePercentage = 0,
            SystemFee = 0,
            OwnerAmount = total,
            PaymentMethod = req.PayNow ? "cliq" : null,
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            Status = req.PayNow ? "pending_payment" : "pending",
            IsManual = true,
            Source = Booking.WebSource,
            PublicToken = NewToken(),
            // Armed either way: an unanswered request, or a deposit that never comes, must not
            // hold the pitch for ever.
            PaymentDeadlineAt = PaymentDeadline.Compute(now, plan.Date, plan.StartTime, req.PayNow ? _expiry : RequestWindow),
        };
        _db.Bookings.Add(booking);

        await _audit.AddAsync("booking.created", venue.OwnerId, "booking", booking.Id,
            $"Web booking {(req.PayNow ? "(paying now)" : "request")} from {name} {plan.Date:yyyy-MM-dd} {plan.StartTime} at {venue.Name}: {AuditLog.Jod(total)}",
            $"حجز من الويب {(req.PayNow ? "(دفع الآن)" : "(طلب)")} من {name} {plan.Date:yyyy-MM-dd} {plan.StartTime} في {venue.Name}: {AuditLog.Jod(total)}");
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        var created = await LoadWebAsync(booking.PublicToken!);
        try { await _notifications.NotifyWebRequest(created!); }
        catch (Exception ex) { _logger.LogWarning(ex, "Web-request notification failed for {BookingId}", booking.Id); }

        return Ok(new ApiResponse<WebBookingCreated>
        {
            Data = new WebBookingCreated { Token = booking.PublicToken! },
            Message = req.PayNow ? "Booking held. Pay the deposit to keep it." : "Request sent to the venue.",
        });
    }

    // GET /api/v1/public/requests/{token}
    [AllowAnonymous]
    [HttpGet("/api/v1/public/requests/{token}")]
    [EnableRateLimiting("web-public")]
    public async Task<IActionResult> WebStatus(string token)
    {
        var booking = await LoadWebAsync(token);
        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });
        return Ok(new ApiResponse<WebBookingStatus> { Data = await WebStatusOf(booking) });
    }

    // POST /api/v1/public/requests/{token}/pay — a request becomes "pay the deposit now"
    [AllowAnonymous]
    [HttpPost("/api/v1/public/requests/{token}/pay")]
    [EnableRateLimiting("web-request")]
    public async Task<IActionResult> WebPayNow(string token)
    {
        var booking = await LoadWebAsync(token, track: true);
        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });
        if (booking.Status != "pending")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This booking is not waiting for confirmation" });
        if (!CanTakeDeposit(booking.Venue))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This venue does not take payment online" });

        booking.Status = "pending_payment";
        booking.PaymentMethod = "cliq";
        booking.PaymentDeadlineAt = PaymentDeadline.Compute(DateTime.UtcNow, booking.Date, booking.StartTime, _expiry);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<WebBookingStatus> { Data = await WebStatusOf(booking) });
    }

    // POST /api/v1/public/requests/{token}/proof — multipart "file": the CliQ transfer screenshot
    [AllowAnonymous]
    [HttpPost("/api/v1/public/requests/{token}/proof")]
    [EnableRateLimiting("web-public")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> WebProof(string token, IFormFile? file)
    {
        var booking = await LoadWebAsync(token, track: true);
        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });
        if (booking.Status != "pending_payment" || booking.PaymentMethod != "cliq")
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This booking is not waiting for a payment" });
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Choose the screenshot of your transfer" });
        if (file.Length > 5 * 1024 * 1024)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "The image is too large (max 5MB)" });

        // The same checks as every uploaded proof: real image bytes, whatever the name says.
        var head = new byte[16];
        int read;
        await using (var probe = file.OpenReadStream())
            read = await probe.ReadAtLeastAsync(head, 12, throwOnEndOfStream: false);
        var ext = ImageExtension(head.AsSpan(0, read));
        if (ext == null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "That file is not an image" });

        var relative = Path.Combine("uploads", "proofs", $"{Guid.NewGuid()}{ext}");
        var absolute = Path.Combine(_contentRoot, "wwwroot", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await using (var stream = new FileStream(absolute, FileMode.Create))
            await file.CopyToAsync(stream);

        booking.PaymentProof = $"{_uploadsBaseUrl}/{relative.Replace('\\', '/')}";
        booking.PaymentProofStatus = "pending_review";
        booking.PaymentProofNote = null;
        booking.Status = "pending_review";
        // Disarmed: the guest has done their part; the slot now waits on the venue's review.
        booking.PaymentDeadlineAt = null;
        await _db.SaveChangesAsync();

        try { await _notifications.NotifyProofReceived(booking); }
        catch (Exception ex) { _logger.LogWarning(ex, "Proof notification failed for {BookingId}", booking.Id); }

        return Ok(new ApiResponse<WebBookingStatus> { Data = await WebStatusOf(booking) });
    }

    // POST /api/v1/public/requests/{token}/cancel
    [AllowAnonymous]
    [HttpPost("/api/v1/public/requests/{token}/cancel")]
    [EnableRateLimiting("web-request")]
    public async Task<IActionResult> WebCancel(string token)
    {
        var booking = await LoadWebAsync(token, track: true);
        if (booking == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Booking not found" });
        if (!GuestMayCancel(booking))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "This booking can no longer be cancelled here. Call the venue." });

        // The venue's rule decides any refund, exactly as for an app player cancelling.
        var refund = CancellationPolicy.RefundFor(booking, booking.Venue, CancellationPolicy.Policy, DateTime.UtcNow);
        booking.Status = "cancelled";
        booking.PaymentDeadlineAt = null;
        if (booking.PaymentProofStatus == "pending_review") booking.PaymentProofStatus = "cancelled";
        if (refund > 0)
        {
            var (row, _) = PaymentLedger.Refund(booking, refund, "refund", null, "Refunded by the cancellation policy");
            if (row != null) _db.Payments.Add(row);
        }
        var who = booking.Customer?.Name ?? "the guest";
        await _audit.AddAsync("booking.cancelled", booking.Venue.OwnerId, "booking", booking.Id,
            $"Cancelled on the web by {who}: {AuditLog.Describe(booking)}" + (refund > 0 ? $", refunded {AuditLog.Jod(refund)}" : ""),
            $"ألغاه {who} من الويب: {AuditLog.Describe(booking)}" + (refund > 0 ? $"، إعادة {AuditLog.Jod(refund)}" : ""));
        await _db.SaveChangesAsync();

        // Nobody on the venue's side did this, so the whole team that follows bookings hears.
        try { await _notifications.NotifyBookingCancelled(booking, cancelledByUserId: ""); }
        catch (Exception ex) { _logger.LogWarning(ex, "Cancel notification failed for {BookingId}", booking.Id); }

        return Ok(new ApiResponse<WebBookingStatus> { Data = await WebStatusOf(booking) });
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private Task<Venue?> FindBySlugAsync(string slug)
    {
        var s = (slug ?? "").Trim().ToLowerInvariant();
        return _db.Venues.FirstOrDefaultAsync(v => v.Slug == s || v.Id == s);
    }

    /// <summary>Taking bookings at all: the same rule that decides whether it is open to the public.</summary>
    private async Task<bool> AcceptsBookingsAsync(Venue venue) =>
        venue.Status == "active"
        && !await _db.Companies.AnyAsync(c => c.OwnerId == venue.OwnerId && c.SuspendedAt != null);

    private static bool CanTakeDeposit(Venue venue) =>
        !string.IsNullOrWhiteSpace(venue.CliqAlias) && venue.DepositPercentage > 0;

    private async Task<Booking?> LoadWebAsync(string token, bool track = false)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        var q = _db.Bookings.Include(b => b.Venue).Include(b => b.Customer).Include(b => b.Player).AsSplitQuery();
        if (!track) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(b => b.PublicToken == token && b.Source == Booking.WebSource);
    }

    private static bool GuestMayCancel(Booking b)
    {
        if (b.Status is not ("pending" or "pending_payment" or "pending_review" or "confirmed")) return false;
        var start = PaymentDeadline.SlotStartUtc(b.Date, b.StartTime);
        return start is null || start > DateTime.UtcNow;
    }

    private async Task<WebBookingStatus> WebStatusOf(Booking b)
    {
        var status = b.Status switch
        {
            "pending" => "requested",
            "pending_payment" => "awaiting_payment",
            "pending_review" => "awaiting_review",
            "cancelled" when b.AutoCancelledAt != null => "expired",
            _ => b.Status,
        };
        var pitch = PitchSizes.ResolvedPitches(b.Venue).FirstOrDefault(p => p.Id == b.PitchId)
            ?? PitchSizes.ResolvedPitches(b.Venue).FirstOrDefault(p => string.Equals(p.Sport, b.Sport, StringComparison.OrdinalIgnoreCase));
        var phone = await _db.Users.Where(u => u.Id == b.Venue.OwnerId).Select(u => u.Phone).FirstOrDefaultAsync();
        var awaitingPayment = b.Status == "pending_payment";
        return new WebBookingStatus
        {
            Status = status,
            VenueName = b.Venue.Name, VenueNameAr = b.Venue.NameAr, VenueSlug = b.Venue.Slug, VenuePhone = phone,
            CustomerName = b.Customer?.Name, Sport = b.Sport, PitchName = pitch?.Name,
            Date = b.Date.ToString("yyyy-MM-dd"), StartTime = b.StartTime, Duration = b.Duration,
            Total = Math.Round(b.TotalAmount, 3), Deposit = Math.Round(b.DepositAmount, 3), Paid = Math.Round(b.AmountPaid, 3),
            CliqAlias = awaitingPayment ? b.Venue.CliqAlias : null,
            Deadline = b.PaymentDeadlineAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ProofNote = awaitingPayment && b.PaymentProofStatus == "rejected" ? b.PaymentProofNote : null,
            CanPayNow = b.Status == "pending" && CanTakeDeposit(b.Venue),
            CanCancel = GuestMayCancel(b),
            FreeCancelHours = b.Venue.FreeCancelHours,
        };
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>The file extension for real image bytes, or null when they are not an image we keep.</summary>
    private static string? ImageExtension(ReadOnlySpan<byte> head)
    {
        if (!ImageBytes.HasAllowedSignature(head)) return null;
        if (head[0] == 0xFF) return ".jpg";
        if (head[0] == 0x89) return ".png";
        if (head[0] == 0x52) return ".webp";
        return ".heic";
    }
}
