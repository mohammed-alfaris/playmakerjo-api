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

namespace SportsVenueApi.Controllers;

[ApiController]
[Route("api/v1/venues")]
[Authorize]
public class VenuesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly string _uploadsBaseUrl;

    public VenuesController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _uploadsBaseUrl = config["Uploads:BaseUrl"]?.TrimEnd('/') ?? "";
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub") ?? "";
    private string UserRole => User.FindFirstValue(ClaimTypes.Role) ?? "";

    private VenueResponse ToDto(Venue v) => new()
    {
        Id = v.Id,
        Name = v.Name,
        Owner = new OwnerRef { Id = v.Owner.Id, Name = v.Owner.Name },
        Sports = v.Sports,
        City = v.City,
        Address = v.Address,
        PricePerHour = v.PricePerHour,
        Status = v.Status,
        Description = v.Description,
        Images = v.Images?.Select(x => UploadUrlHelper.Normalize(x, _uploadsBaseUrl)).ToList()!,
        Latitude = v.Latitude,
        Longitude = v.Longitude,
        CliqAlias = v.CliqAlias,
        OperatingHours = v.OperatingHours,
        MinBookingDuration = v.MinBookingDuration,
        MaxBookingDuration = v.MaxBookingDuration,
        DepositPercentage = v.DepositPercentage,
        ParentSize = v.ParentSize,
        SubSizes = v.SubSizes,
        SizePrices = v.SizePrices,
        SportsConfig = v.SportsConfig,
        SportsIsolated = v.SportsIsolated,
        Pitches = PitchSizes.ResolvedPitches(v),
        CreatedAt = v.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
    };

    /// <summary>
    /// Validate + normalize a list of pitches for a venue: mint UUIDs for new pitches,
    /// enforce non-empty names and uniqueness, require a sport, validate subdivision
    /// rules, and require sizePrices entries for every offered sub-size. Returns
    /// a human-readable error string or null on success (in-place mutation of the list).
    /// </summary>
    private static string? ValidateAndNormalizePitches(List<PitchDto>? pitches)
    {
        if (pitches == null || pitches.Count == 0) return null;

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pitches)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
                return "Every pitch needs a name.";
            if (!seenNames.Add(p.Name.Trim()))
                return $"Duplicate pitch name: '{p.Name}'. Pitch names must be unique within a venue.";
            if (string.IsNullOrWhiteSpace(p.Sport))
                return $"Pitch '{p.Name}' is missing a sport.";

            var isFootball = string.Equals(p.Sport, "football", StringComparison.OrdinalIgnoreCase);
            if (isFootball)
            {
                if (string.IsNullOrEmpty(p.ParentSize))
                    return $"Football pitch '{p.Name}' needs a pitch size (5, 6, 7, 8, or 11).";

                var err = PitchSizes.ValidateSubSizes(p.ParentSize, p.SubSizes ?? []);
                if (err != null)
                    return $"Pitch '{p.Name}': {err}";

                foreach (var sz in p.SubSizes ?? [])
                {
                    if (!p.SizePrices.TryGetValue(sz, out var pr) || pr <= 0)
                        return $"Pitch '{p.Name}': missing price for {sz}-aside.";
                }
            }
            else
            {
                // Non-football pitches must not carry football subdivision config.
                if (!string.IsNullOrEmpty(p.ParentSize) || (p.SubSizes?.Count ?? 0) > 0)
                    return $"Pitch '{p.Name}' ({p.Sport}): pitch-size / split is football-only.";
            }

            if (p.PricePerHour < 0)
                return $"Pitch '{p.Name}' has a negative price.";

            // Mint a UUID for brand-new pitches. Accept any existing id (dashboard
            // sends back the stable ids on edit so bookings stay linked).
            if (string.IsNullOrWhiteSpace(p.Id))
                p.Id = "p_" + Guid.NewGuid().ToString("N")[..10];

            p.Name = p.Name.Trim();
            p.SubSizes ??= [];
            p.SizePrices ??= [];
        }

        return null;
    }

    // Split (subdividable pitch) is football-only. Validate both the venue-level
    // legacy fields and any per-sport override inside sports_config.
    private static string? ValidateSplitScope(List<string> sports, string? venueParentSize,
                                              Dictionary<string, SportConfigDto>? sportsConfig)
    {
        var hasFootball = sports.Any(s => string.Equals(s, "football", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(venueParentSize) && !hasFootball)
            return "Pitch size / split is only supported for football venues.";

        if (sportsConfig != null)
        {
            foreach (var (sport, cfg) in sportsConfig)
            {
                var hasSplit = !string.IsNullOrEmpty(cfg?.ParentSize)
                               || (cfg?.SubSizes?.Count ?? 0) > 0;
                if (hasSplit && !string.Equals(sport, "football", StringComparison.OrdinalIgnoreCase))
                    return $"Pitch size / split config is only allowed for 'football', not '{sport}'.";
            }
        }

        return null;
    }

    /// <summary>Stamp averageRating + reviewCount onto a batch of venue DTOs from non-hidden reviews.</summary>
    private async Task StampAggregatesAsync(List<VenueResponse> dtos)
    {
        if (dtos.Count == 0) return;
        var ids = dtos.Select(d => d.Id).ToList();
        var stats = await _db.Reviews
            .Where(r => ids.Contains(r.VenueId) && !r.Hidden)
            .GroupBy(r => r.VenueId)
            .Select(g => new { VenueId = g.Key, Avg = g.Average(x => (double)x.Rating), Count = g.Count() })
            .ToListAsync();
        var map = stats.ToDictionary(s => s.VenueId, s => s);
        foreach (var d in dtos)
        {
            if (map.TryGetValue(d.Id, out var s))
            {
                d.AverageRating = Math.Round(s.Avg, 2);
                d.ReviewCount = s.Count;
            }
            else
            {
                d.AverageRating = null;
                d.ReviewCount = 0;
            }
        }
    }

    /// <summary>Stamp averageRating + reviewCount on a single venue DTO.</summary>
    private async Task StampAggregateAsync(VenueResponse dto)
    {
        var stat = await _db.Reviews
            .Where(r => r.VenueId == dto.Id && !r.Hidden)
            .GroupBy(r => r.VenueId)
            .Select(g => new { Avg = g.Average(x => (double)x.Rating), Count = g.Count() })
            .FirstOrDefaultAsync();

        if (stat == null)
        {
            dto.AverageRating = null;
            dto.ReviewCount = 0;
        }
        else
        {
            dto.AverageRating = Math.Round(stat.Avg, 2);
            dto.ReviewCount = stat.Count;
        }
    }

    // ── Public endpoints (no auth required) ──

    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<IActionResult> PublicList(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? sport = null,
        [FromQuery] string? city = null)
    {
        var baseQuery = _db.Venues.Where(v => v.Status == "active");

        if (!string.IsNullOrEmpty(search))
            baseQuery = baseQuery.Where(v => EF.Functions.Like(v.Name, $"%{search}%")
                                  || EF.Functions.Like(v.City!, $"%{search}%"));

        if (!string.IsNullOrEmpty(sport))
            baseQuery = baseQuery.Where(v => v.SportsJson.Contains($"\"{sport}\""));

        if (!string.IsNullOrEmpty(city))
            baseQuery = baseQuery.Where(v => v.City == city);

        var total = await baseQuery.CountAsync();
        var venues = await baseQuery
            .Include(v => v.Owner)
            .AsSplitQuery()
            .OrderByDescending(v => v.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var dtos = venues.Select(ToDto).ToList();
        await StampAggregatesAsync(dtos);

        return Ok(new ApiResponse<List<VenueResponse>>
        {
            Data = dtos,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    [AllowAnonymous]
    [HttpGet("public/{venueId}")]
    public async Task<IActionResult> PublicGet(string venueId)
    {
        var venue = await _db.Venues
            .Include(v => v.Owner)
            .FirstOrDefaultAsync(v => v.Id == venueId && v.Status == "active");

        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var dto = ToDto(venue);
        await StampAggregateAsync(dto);
        return Ok(new ApiResponse<VenueResponse> { Data = dto });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? search = null,
        [FromQuery] string? sport = null,
        [FromQuery] string? status = null,
        [FromQuery] string? owner_id = null)
    {
        var ownerId = owner_id;
        if (UserRole == "venue_owner")
            ownerId = UserId;

        var baseQuery = _db.Venues.AsQueryable();

        if (!string.IsNullOrEmpty(ownerId))
            baseQuery = baseQuery.Where(v => v.OwnerId == ownerId);

        if (!string.IsNullOrEmpty(search))
            baseQuery = baseQuery.Where(v => EF.Functions.Like(v.Name, $"%{search}%")
                                  || EF.Functions.Like(v.City!, $"%{search}%"));

        if (!string.IsNullOrEmpty(sport))
            baseQuery = baseQuery.Where(v => v.SportsJson.Contains($"\"{sport}\""));

        if (!string.IsNullOrEmpty(status))
            baseQuery = baseQuery.Where(v => v.Status == status);

        var total = await baseQuery.CountAsync();
        var venues = await baseQuery
            .Include(v => v.Owner)
            .AsSplitQuery()
            .OrderByDescending(v => v.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        var dtos = venues.Select(ToDto).ToList();
        await StampAggregatesAsync(dtos);

        return Ok(new ApiResponse<List<VenueResponse>>
        {
            Data = dtos,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] VenueCreateRequest req)
    {
        var ownerId = req.OwnerId ?? UserId;
        if (UserRole == "venue_owner")
            ownerId = UserId;

        // Split config is football-only — reject split settings on non-football venues.
        var scopeErr = ValidateSplitScope(req.Sports, req.ParentSize, req.SportsConfig);
        if (scopeErr != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = scopeErr });

        // Validate venue-level pitch-size fields (legacy / single-sport path)
        if (req.ParentSize != null)
        {
            var subs = req.SubSizes ?? [];
            var err = PitchSizes.ValidateSubSizes(req.ParentSize, subs);
            if (err != null)
                return BadRequest(new ApiResponse<object> { Success = false, Message = err });

            // If sub-sizes are enabled, require a size_prices entry for each offered size
            var offered = new HashSet<string> { req.ParentSize };
            foreach (var s in subs) offered.Add(s);
            var prices = req.SizePrices ?? [];
            foreach (var sz in offered)
            {
                if (sz == req.ParentSize) continue; // parent uses PricePerHour
                if (!prices.TryGetValue(sz, out var p) || p <= 0)
                    return BadRequest(new ApiResponse<object> { Success = false, Message = $"Missing price for {sz}-aside." });
            }
        }

        // Validate per-sport football split config (multi-sport path)
        if (req.SportsConfig != null &&
            req.SportsConfig.TryGetValue("football", out var footballCfg) &&
            !string.IsNullOrEmpty(footballCfg?.ParentSize))
        {
            var subs = footballCfg.SubSizes ?? [];
            var err = PitchSizes.ValidateSubSizes(footballCfg.ParentSize!, subs);
            if (err != null)
                return BadRequest(new ApiResponse<object> { Success = false, Message = err });

            var prices = footballCfg.SizePrices ?? [];
            foreach (var sz in subs)
            {
                if (!prices.TryGetValue(sz, out var p) || p <= 0)
                    return BadRequest(new ApiResponse<object> { Success = false, Message = $"Missing price for {sz}-aside (football)." });
            }
        }

        // Multi-pitch venue: validate every pitch and mint UUIDs for new ones.
        var pitchErr = ValidateAndNormalizePitches(req.Pitches);
        if (pitchErr != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = pitchErr });

        var venue = new Venue
        {
            Name = req.Name,
            OwnerId = ownerId,
            Sports = req.Sports,
            City = req.City,
            Address = req.Address,
            PricePerHour = req.PricePerHour,
            Status = req.Status,
            Description = req.Description,
            Images = req.Images,
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            CliqAlias = req.CliqAlias,
            ParentSize = req.ParentSize,
            SubSizes = req.SubSizes ?? [],
            SizePrices = req.SizePrices ?? []
        };
        if (req.OperatingHours != null)
            venue.OperatingHoursJson = JsonSerializer.Serialize(req.OperatingHours);
        if (req.MinBookingDuration.HasValue)
            venue.MinBookingDuration = req.MinBookingDuration.Value;
        if (req.MaxBookingDuration.HasValue)
            venue.MaxBookingDuration = req.MaxBookingDuration.Value;
        if (req.DepositPercentage.HasValue)
            venue.DepositPercentage = req.DepositPercentage.Value;
        if (req.SportsConfig != null)
            venue.SportsConfig = req.SportsConfig;
        if (req.SportsIsolated.HasValue)
            venue.SportsIsolated = req.SportsIsolated.Value;
        if (req.Pitches != null)
            venue.Pitches = req.Pitches;

        _db.Venues.Add(venue);
        await _db.SaveChangesAsync();

        // Reload with owner
        var created = await _db.Venues.Include(v => v.Owner).FirstAsync(v => v.Id == venue.Id);

        return Ok(new ApiResponse<VenueResponse> { Data = ToDto(created), Message = "Venue created" });
    }

    [HttpGet("{venueId}")]
    public async Task<IActionResult> Get(string venueId)
    {
        var venue = await _db.Venues.Include(v => v.Owner).FirstOrDefaultAsync(v => v.Id == venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var dto = ToDto(venue);
        await StampAggregateAsync(dto);
        return Ok(new ApiResponse<VenueResponse> { Data = dto });
    }

    [HttpPatch("{venueId}")]
    public async Task<IActionResult> Update(string venueId, [FromBody] VenueUpdateRequest req)
    {
        var venue = await _db.Venues.Include(v => v.Owner).FirstOrDefaultAsync(v => v.Id == venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (req.Name != null) venue.Name = req.Name;
        if (req.OwnerId != null) venue.OwnerId = req.OwnerId;
        if (req.Sports != null) venue.Sports = req.Sports;
        if (req.City != null) venue.City = req.City;
        if (req.Address != null) venue.Address = req.Address;
        if (req.PricePerHour.HasValue) venue.PricePerHour = req.PricePerHour.Value;
        if (req.Status != null) venue.Status = req.Status;
        if (req.Description != null) venue.Description = req.Description;
        if (req.Images != null) venue.Images = req.Images;
        if (req.Latitude.HasValue) venue.Latitude = req.Latitude;
        if (req.Longitude.HasValue) venue.Longitude = req.Longitude;
        if (req.CliqAlias != null) venue.CliqAlias = req.CliqAlias;
        if (req.OperatingHours != null) venue.OperatingHoursJson = JsonSerializer.Serialize(req.OperatingHours);
        if (req.MinBookingDuration.HasValue) venue.MinBookingDuration = req.MinBookingDuration.Value;
        if (req.MaxBookingDuration.HasValue) venue.MaxBookingDuration = req.MaxBookingDuration.Value;
        if (req.DepositPercentage.HasValue) venue.DepositPercentage = req.DepositPercentage.Value;

        // Pitch-size fields — validate together if any of them is being updated
        if (req.ParentSize != null || req.SubSizes != null || req.SizePrices != null)
        {
            var newParent = req.ParentSize ?? venue.ParentSize;
            var newSubs = req.SubSizes ?? venue.SubSizes;
            var newPrices = req.SizePrices ?? venue.SizePrices;

            if (newParent != null)
            {
                var err = PitchSizes.ValidateSubSizes(newParent, newSubs);
                if (err != null)
                    return BadRequest(new ApiResponse<object> { Success = false, Message = err });

                var offered = new HashSet<string> { newParent };
                foreach (var s in newSubs) offered.Add(s);
                foreach (var sz in offered)
                {
                    if (sz == newParent) continue;
                    if (!newPrices.TryGetValue(sz, out var p) || p <= 0)
                        return BadRequest(new ApiResponse<object> { Success = false, Message = $"Missing price for {sz}-aside." });
                }

                venue.ParentSize = newParent;
                venue.SubSizes = newSubs;
                venue.SizePrices = newPrices;
            }
            else
            {
                // Clearing back to legacy single-size
                venue.ParentSize = null;
                venue.SubSizes = [];
                venue.SizePrices = [];
            }
        }

        // Per-sport config + isolation toggle
        if (req.SportsConfig != null)
        {
            var footballCfg = req.SportsConfig.GetValueOrDefault("football");
            if (footballCfg != null && !string.IsNullOrEmpty(footballCfg.ParentSize))
            {
                var subs = footballCfg.SubSizes ?? [];
                var err = PitchSizes.ValidateSubSizes(footballCfg.ParentSize!, subs);
                if (err != null)
                    return BadRequest(new ApiResponse<object> { Success = false, Message = err });

                var prices = footballCfg.SizePrices ?? [];
                foreach (var sz in subs)
                {
                    if (!prices.TryGetValue(sz, out var p) || p <= 0)
                        return BadRequest(new ApiResponse<object> { Success = false, Message = $"Missing price for {sz}-aside (football)." });
                }
            }

            venue.SportsConfig = req.SportsConfig;
        }

        if (req.SportsIsolated.HasValue)
            venue.SportsIsolated = req.SportsIsolated.Value;

        // Multi-pitch: validate + normalize before writing.
        if (req.Pitches != null)
        {
            var pitchErr = ValidateAndNormalizePitches(req.Pitches);
            if (pitchErr != null)
                return BadRequest(new ApiResponse<object> { Success = false, Message = pitchErr });
            venue.Pitches = req.Pitches;
        }

        // Final cross-field check: split is football-only (both legacy fields + per-sport config)
        var effectiveSports = req.Sports ?? venue.Sports;
        var scopeErr = ValidateSplitScope(effectiveSports, venue.ParentSize, venue.SportsConfig);
        if (scopeErr != null)
            return BadRequest(new ApiResponse<object> { Success = false, Message = scopeErr });

        await _db.SaveChangesAsync();

        // Reload owner if changed
        if (req.OwnerId != null)
            await _db.Entry(venue).Reference(v => v.Owner).LoadAsync();

        return Ok(new ApiResponse<VenueResponse> { Data = ToDto(venue), Message = "Venue updated" });
    }

    [HttpDelete("{venueId}")]
    public async Task<IActionResult> Delete(string venueId)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        _db.Venues.Remove(venue);
        await _db.SaveChangesAsync();

        return Ok(new ApiResponse<object> { Data = null, Message = "Venue deleted" });
    }

    // GET /api/v1/venues/{venueId}/available-slots?date=2025-04-10&pitchId=...
    // When pitchId is omitted on a multi-pitch venue, the response carries a
    // <c>pitches</c> array with per-pitch booked slots so the client can render
    // the whole day at once.
    [AllowAnonymous]
    [HttpGet("{venueId}/available-slots")]
    public async Task<IActionResult> AvailableSlots(
        string venueId,
        [FromQuery] string? date = null,
        [FromQuery] string? pitchId = null)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date, out var bookingDate))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid or missing date. Use YYYY-MM-DD" });

        // Venue-level operating hours (fallback when a pitch doesn't override them).
        // ResolveHoursForDay accepts both full day names and 3-letter abbreviations
        // and honours the "closed: true" flag written by the dashboard editor.
        var dayName = bookingDate.DayOfWeek.ToString().ToLower();
        OperatingHoursInfo? venueHours = ResolveHoursForDay(venue.OperatingHours, dayName);

        // Get existing bookings for that day (non-cancelled)
        var existingBookings = await _db.Bookings
            .Where(b => b.VenueId == venueId
                && b.Date.Date == bookingDate.Date
                && b.Status != "cancelled")
            .ToListAsync();

        var pitches = PitchSizes.ResolvedPitches(venue);

        // Single-pitch request: build the response scoped to that pitch only and
        // keep the legacy top-level shape so old clients keep working.
        if (!string.IsNullOrEmpty(pitchId))
        {
            var pitch = pitches.FirstOrDefault(p => p.Id == pitchId);
            if (pitch == null)
                return NotFound(new ApiResponse<object> { Success = false, Message = "Pitch not found" });

            var resp = BuildAvailabilityForPitch(venue, pitch, existingBookings, venueHours, bookingDate);
            return Ok(new ApiResponse<AvailableSlotsResponse> { Data = resp });
        }

        // Multi-pitch / no-pitchId: return legacy top-level (for back-compat with
        // old single-pitch clients) AND a `pitches` array so the new clients can
        // render per-pitch availability.
        var parent = venue.ParentSize;
        var legacyBookedSlots = existingBookings
            .Where(b => !string.IsNullOrEmpty(b.StartTime))
            .Select(b => new BookedSlotInfo
            {
                StartTime = b.StartTime!,
                Duration = b.Duration,
                Sport = b.Sport,
                PitchId = b.PitchId,
                PitchSize = b.PitchSize ?? parent,
                UnitWeight = PitchSizes.WeightOf(b.PitchSize ?? parent)
            })
            .OrderBy(s => s.StartTime)
            .ToList();

        var legacyOffered = PitchSizes.OfferedSizesForSport(venue, "football");
        var legacyCapacity = PitchSizes.CapacityOfForSport(venue, "football");

        var perPitch = pitches
            .Select(p => BuildPitchAvailability(venue, p, existingBookings, venueHours, bookingDate))
            .ToList();

        return Ok(new ApiResponse<AvailableSlotsResponse>
        {
            Data = new AvailableSlotsResponse
            {
                VenueId = venueId,
                Date = bookingDate.ToString("yyyy-MM-dd"),
                OperatingHours = venueHours,
                BookedSlots = legacyBookedSlots,
                PricePerHour = venue.PricePerHour,
                MinDuration = venue.MinBookingDuration,
                MaxDuration = venue.MaxBookingDuration,
                DepositPercentage = venue.DepositPercentage,
                ParentSize = parent,
                OfferedSizes = legacyOffered,
                SizePrices = venue.SizePrices,
                CapacityUnits = legacyCapacity,
                Pitches = perPitch
            }
        });
    }

    private static OperatingHoursInfo? ResolveHoursForDay(Dictionary<string, object>? hoursMap, string dayName)
    {
        if (hoursMap == null) return null;

        // The dashboard writes keys as full day names ("monday", "tuesday", ...)
        // while older seed data used 3-letter abbreviations ("mon", "tue", ...).
        // Accept both so legacy venues and newly-edited ones keep working.
        var dayFull = dayName.ToLower();
        var dayShort = dayFull.Length >= 3 ? dayFull[..3] : dayFull;
        if (!hoursMap.TryGetValue(dayFull, out var dayHoursObj)
            && !hoursMap.TryGetValue(dayShort, out dayHoursObj))
            return null;

        var dayHoursJson = JsonSerializer.Serialize(dayHoursObj);
        var dayHours = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dayHoursJson);
        if (dayHours == null) return null;

        // Honour the "closed: true" flag written by the dashboard editor — if
        // the day is marked closed there is no open/close window for it.
        if (dayHours.TryGetValue("closed", out var closedEl)
            && closedEl.ValueKind == JsonValueKind.True)
            return null;

        string GetStr(string k, string fallback)
            => dayHours.TryGetValue(k, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;

        return new OperatingHoursInfo
        {
            Open = GetStr("open", "08:00"),
            Close = GetStr("close", "22:00")
        };
    }

    private static OperatingHoursInfo? ResolvePitchHours(PitchDto pitch, string dayName, OperatingHoursInfo? fallback)
    {
        if (pitch.OperatingHours == null) return fallback;
        try
        {
            var json = JsonSerializer.Serialize(pitch.OperatingHours);
            var map = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (map == null) return fallback;
            return ResolveHoursForDay(map, dayName) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Build the single-pitch AvailableSlotsResponse (legacy shape, no <c>pitches</c> array).
    /// </summary>
    private static AvailableSlotsResponse BuildAvailabilityForPitch(
        Venue v, PitchDto pitch, List<Booking> allBookings,
        OperatingHoursInfo? venueHours, DateTime bookingDate)
    {
        // ResolveHoursForDay accepts both full and short day names.
        var dayName = bookingDate.DayOfWeek.ToString().ToLower();
        var hours = ResolvePitchHours(pitch, dayName, venueHours);

        var pitchBookings = allBookings
            .Where(b => MatchesPitch(b, v, pitch))
            .ToList();

        var booked = pitchBookings
            .Where(b => !string.IsNullOrEmpty(b.StartTime))
            .Select(b => new BookedSlotInfo
            {
                StartTime = b.StartTime!,
                Duration = b.Duration,
                Sport = b.Sport,
                PitchId = pitch.Id,
                PitchSize = b.PitchSize ?? pitch.ParentSize,
                UnitWeight = PitchSizes.WeightOf(b.PitchSize ?? pitch.ParentSize)
            })
            .OrderBy(s => s.StartTime)
            .ToList();

        var offered = PitchSizes.OfferedSizes(pitch);
        var capacity = PitchSizes.CapacityOf(pitch);

        return new AvailableSlotsResponse
        {
            VenueId = v.Id,
            Date = bookingDate.ToString("yyyy-MM-dd"),
            OperatingHours = hours,
            BookedSlots = booked,
            PricePerHour = pitch.PricePerHour > 0 ? pitch.PricePerHour : v.PricePerHour,
            MinDuration = v.MinBookingDuration,
            MaxDuration = v.MaxBookingDuration,
            DepositPercentage = v.DepositPercentage,
            ParentSize = pitch.ParentSize,
            OfferedSizes = offered,
            SizePrices = pitch.SizePrices,
            CapacityUnits = capacity
        };
    }

    /// <summary>Build a PitchAvailability entry for inclusion in the multi-pitch response.</summary>
    private static PitchAvailability BuildPitchAvailability(
        Venue v, PitchDto pitch, List<Booking> allBookings,
        OperatingHoursInfo? venueHours, DateTime bookingDate)
    {
        // ResolveHoursForDay accepts both full and short day names.
        var dayName = bookingDate.DayOfWeek.ToString().ToLower();
        var hours = ResolvePitchHours(pitch, dayName, venueHours);

        var pitchBookings = allBookings
            .Where(b => MatchesPitch(b, v, pitch))
            .Where(b => !string.IsNullOrEmpty(b.StartTime))
            .Select(b => new BookedSlotInfo
            {
                StartTime = b.StartTime!,
                Duration = b.Duration,
                Sport = b.Sport,
                PitchId = pitch.Id,
                PitchSize = b.PitchSize ?? pitch.ParentSize,
                UnitWeight = PitchSizes.WeightOf(b.PitchSize ?? pitch.ParentSize)
            })
            .OrderBy(s => s.StartTime)
            .ToList();

        return new PitchAvailability
        {
            PitchId = pitch.Id,
            Name = pitch.Name,
            Sport = pitch.Sport,
            ParentSize = pitch.ParentSize,
            OfferedSizes = PitchSizes.OfferedSizes(pitch),
            SizePrices = pitch.SizePrices,
            PricePerHour = pitch.PricePerHour > 0 ? pitch.PricePerHour : v.PricePerHour,
            CapacityUnits = PitchSizes.CapacityOf(pitch),
            OperatingHours = hours,
            BookedSlots = pitchBookings
        };
    }

    /// <summary>
    /// A booking belongs to a pitch when the explicit pitch_id matches OR — on
    /// legacy rows where pitch_id is null — when this pitch is the first pitch
    /// of the booking's sport on the resolved pitch list. This makes legacy
    /// bookings appear on exactly one timeline (never duplicated), and makes
    /// venues with empty <c>pitches</c> (implicit single-pitch) behave exactly
    /// as today.
    /// </summary>
    private static bool MatchesPitch(Booking b, Venue v, PitchDto pitch)
    {
        if (!string.IsNullOrEmpty(b.PitchId))
            return b.PitchId == pitch.Id;
        var firstOfSport = PitchSizes.ResolvedPitches(v)
            .FirstOrDefault(p => string.Equals(p.Sport, b.Sport, StringComparison.OrdinalIgnoreCase));
        return firstOfSport != null && firstOfSport.Id == pitch.Id;
    }

    [HttpGet("{venueId}/stats")]
    public async Task<IActionResult> Stats(string venueId)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        var totalBookings = await _db.Bookings.CountAsync(b => b.VenueId == venueId);
        var totalRevenue = await _db.Bookings
            .Where(b => b.VenueId == venueId && b.Status == "completed")
            .SumAsync(b => b.Amount);

        return Ok(new ApiResponse<VenueStatsResponse>
        {
            Data = new VenueStatsResponse
            {
                TotalBookings = totalBookings,
                TotalRevenue = totalRevenue,
                ActiveSince = venue.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
            }
        });
    }
}
