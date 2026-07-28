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

    /// <summary>The venue_owner a staff caller works for. Null for every other role.</summary>
    private string? StaffOwnerId => User.FindFirstValue("owner_id");

    /// <summary>The owner whose venues this caller belongs to — themselves, or their boss.</summary>
    private string? EffectiveOwnerId => UserRole switch
    {
        "venue_owner" => UserId,
        "venue_staff" => StaffOwnerId,
        _ => null,
    };

    /// <summary>
    /// The venue as an outsider may see it: everything needed to browse, compare and book,
    /// with the owner's CliQ alias removed.
    ///
    /// The alias is the owner's payment identifier — the string customers transfer money to.
    /// It was reachable with NO authentication at all through /venues/public and
    /// /venues/public/{id}, and venue ids are enumerable from the list route, so every
    /// alias on the platform could be harvested in one pass. It is not a secret in the way
    /// a password is (a paying customer must see it), but bulk-harvestable is a different
    /// thing from visible-to-someone-who-is-paying-you: it is exactly what is needed to
    /// impersonate a venue and substitute a different alias.
    ///
    /// Stripping it here rather than in each route is deliberate. Three anonymous endpoints
    /// serve this shape today and the fourth that gets added next year will be safe by
    /// default — the leak happened because a route forgot, not because anyone decided.
    /// </summary>
    private VenueResponse ToPublicDto(Venue v)
    {
        var dto = ToDto(v);
        // Verified by removal: commenting this single line fails five of the seven
        // VenueDetailLeakTests, including the anonymous ones.
        dto.CliqAlias = null;
        return dto;
    }

    private VenueResponse ToDto(Venue v) => new()
    {
        Id = v.Id,
        Name = v.Name,
        NameAr = v.NameAr,
        Owner = new OwnerRef { Id = v.Owner.Id, Name = v.Owner.Name },
        Sports = v.Sports,
        City = v.City,
        CityAr = v.CityAr,
        Address = v.Address,
        AddressAr = v.AddressAr,
        PricePerHour = v.PricePerHour,
        Status = v.Status,
        Description = v.Description,
        DescriptionAr = v.DescriptionAr,
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

        var dtos = venues.Select(ToPublicDto).ToList();
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

        var dto = ToPublicDto(venue);
        await StampAggregateAsync(dto);
        return Ok(new ApiResponse<VenueResponse> { Data = dto });
    }

    // GET /api/v1/venues/search?date=2025-04-10&startTime=18:00&duration=90&sport=football&city=Amman
    // Availability search: active venues with at least one pitch (sport-matching
    // when sport is given) that can take a booking covering the requested window
    // on that date. Same response shape as /venues/public.
    [AllowAnonymous]
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string? date = null,
        [FromQuery] string? startTime = null,
        [FromQuery] int duration = 60,
        [FromQuery] string? sport = null,
        [FromQuery] string? city = null,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        if (string.IsNullOrEmpty(date) || !DateTime.TryParse(date, out var bookingDate))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid or missing date. Use YYYY-MM-DD" });

        if (string.IsNullOrEmpty(startTime) || !TimeSpan.TryParse(startTime, out var start))
            return BadRequest(new ApiResponse<object> { Success = false, Message = "Invalid or missing startTime. Use HH:mm" });

        if (duration <= 0)
            return BadRequest(new ApiResponse<object> { Success = false, Message = "duration must be a positive number of minutes" });

        if (page < 1) page = 1;
        if (limit < 1) limit = 20;
        if (limit > 50) limit = 50;

        var baseQuery = _db.Venues.Where(v => v.Status == "active");

        if (!string.IsNullOrEmpty(sport))
            baseQuery = baseQuery.Where(v => v.SportsJson.Contains($"\"{sport}\""));

        if (!string.IsNullOrEmpty(city))
            baseQuery = baseQuery.Where(v => v.City == city);

        var candidates = await baseQuery
            .Include(v => v.Owner)
            .AsSplitQuery()
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync();

        var venueIds = candidates.Select(v => v.Id).ToList();
        var dayBookings = await _db.Bookings
            .Where(b => venueIds.Contains(b.VenueId)
                && b.Date.Date == bookingDate.Date
                && b.Status != "cancelled")
            .ToListAsync();
        var dow = (int)bookingDate.DayOfWeek;
        var dayPermanents = await _db.PermanentBookings
            .Where(p => venueIds.Contains(p.VenueId) && p.Status == "active" && p.DayOfWeek == dow)
            .ToListAsync();

        var bookingsByVenue = dayBookings.GroupBy(b => b.VenueId).ToDictionary(g => g.Key, g => g.ToList());
        var permanentsByVenue = dayPermanents.GroupBy(p => p.VenueId).ToDictionary(g => g.Key, g => g.ToList());

        var dayName = bookingDate.DayOfWeek.ToString().ToLower();
        var available = candidates.Where(v =>
        {
            var vBookings = bookingsByVenue.GetValueOrDefault(v.Id) ?? [];
            var vPermanents = permanentsByVenue.GetValueOrDefault(v.Id) ?? [];
            return PitchSizes.ResolvedPitches(v)
                .Where(p => string.IsNullOrEmpty(sport) || string.Equals(p.Sport, sport, StringComparison.OrdinalIgnoreCase))
                .Any(p => AvailabilityHelper.PitchHasCapacity(v, p, start, duration, dayName, vBookings, vPermanents));
        }).ToList();

        var total = available.Count;
        var dtos = available
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(ToPublicDto)
            .ToList();
        await StampAggregatesAsync(dtos);

        return Ok(new ApiResponse<List<VenueResponse>>
        {
            Data = dtos,
            Pagination = new PaginationInfo { Page = page, Limit = limit, Total = total }
        });
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
        // Deny-by-default. The old shape pinned ownerId only for venue_owner and then
        // applied the filter only when it was non-empty, so a player or a staff account
        // received every venue on the platform — including competitors' pricing and CliQ
        // aliases. Public discovery lives on the [AllowAnonymous] /venues/public routes;
        // this one is the back office.
        var baseQuery = _db.Venues.AsQueryable();

        if (UserRole == "super_admin")
        {
            if (!string.IsNullOrEmpty(owner_id))
                baseQuery = baseQuery.Where(v => v.OwnerId == owner_id);
        }
        else if (EffectiveOwnerId is { Length: > 0 } scopedOwnerId)
        {
            // Owners see their own; staff see their employer's. The query string is ignored.
            baseQuery = baseQuery.Where(v => v.OwnerId == scopedOwnerId);
        }
        else
        {
            return Forbid();
        }

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
        if (UserRole != "super_admin" && UserRole != "venue_owner")
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Only admins and venue owners can create venues" });

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
            NameAr = req.NameAr,
            OwnerId = ownerId,
            Sports = req.Sports,
            City = req.City,
            CityAr = req.CityAr,
            Address = req.Address,
            AddressAr = req.AddressAr,
            PricePerHour = req.PricePerHour,
            Status = req.Status,
            Description = req.Description,
            DescriptionAr = req.DescriptionAr,
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

        // Not 403 for a non-owner: a player legitimately opens a venue page to book it.
        // What changes is WHICH shape they get. This route had no ownership check at all,
        // so any logged-in account — including a competing venue owner — could read another
        // venue's CliQ alias by id, and ids are enumerable from the public list.
        var dto = VenueAccess.CanView(venue, UserId, UserRole, StaffOwnerId)
            ? ToDto(venue)
            : ToPublicDto(venue);

        await StampAggregateAsync(dto);
        return Ok(new ApiResponse<VenueResponse> { Data = dto });
    }

    [HttpPatch("{venueId}")]
    public async Task<IActionResult> Update(string venueId, [FromBody] VenueUpdateRequest req)
    {
        var venue = await _db.Venues.Include(v => v.Owner).FirstOrDefaultAsync(v => v.Id == venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        if (!VenueAccess.CanManage(venue, UserId, UserRole))
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "You do not have permission to manage this venue" });

        // Ownership reassignment is admin-only. Echoing back the current owner is a no-op.
        if (req.OwnerId != null && req.OwnerId != venue.OwnerId)
        {
            if (UserRole != "super_admin")
                return StatusCode(403, new ApiResponse<object> { Success = false, Message = "Only admins can reassign venue ownership" });
            venue.OwnerId = req.OwnerId;
        }

        if (req.Name != null) venue.Name = req.Name;
        if (req.NameAr != null) venue.NameAr = req.NameAr;
        if (req.Sports != null) venue.Sports = req.Sports;
        if (req.City != null) venue.City = req.City;
        if (req.CityAr != null) venue.CityAr = req.CityAr;
        if (req.Address != null) venue.Address = req.Address;
        if (req.AddressAr != null) venue.AddressAr = req.AddressAr;
        if (req.PricePerHour.HasValue) venue.PricePerHour = req.PricePerHour.Value;
        if (req.Status != null) venue.Status = req.Status;
        if (req.Description != null) venue.Description = req.Description;
        if (req.DescriptionAr != null) venue.DescriptionAr = req.DescriptionAr;
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

        // Multi-pitch: validate + normalize before writing.
        if (req.Pitches != null)
        {
            var pitchErr = ValidateAndNormalizePitches(req.Pitches);
            if (pitchErr != null)
                return BadRequest(new ApiResponse<object> { Success = false, Message = pitchErr });

            // Refuse to strand live bookings on a pitch that is being deleted.
            //
            // This is a double-sell, not a display bug. Every conflict filter matches a
            // booking to a pitch by exact id — AvailabilityHelper.MatchesPitch:195,
            // BookingsController.BookingOnPitch:1657, PermanentBookingsController:329 — and
            // the legacy "first pitch of this sport" fallback fires only for a NULL pitch_id,
            // never for a dangling one. So a booking whose pitch has been removed stops
            // matching any pitch, drops out of every capacity sum, and the hour it occupies
            // is offered for sale again while the customer still holds it.
            //
            // Read venue.Pitches BEFORE the assignment below: it is a [NotMapped] getter that
            // re-deserialises the JSON column on each access, so afterwards the old list is
            // simply gone.
            var keptIds = req.Pitches.Select(p => p.Id!).ToHashSet(StringComparer.Ordinal);
            var removedIds = venue.Pitches
                .Select(p => p.Id!)
                .Where(id => !string.IsNullOrEmpty(id) && !keptIds.Contains(id))
                .ToList();

            if (removedIds.Count > 0)
            {
                // Only the future contends for a slot. Past bookings keep their dead pitch id
                // — they are history — so a pitch can still be retired once its diary is clear.
                var today = PlatformConstants.JordanToday();
                var liveBookings = await _db.Bookings.CountAsync(b =>
                    b.VenueId == venue.Id
                    && b.PitchId != null && removedIds.Contains(b.PitchId)
                    && b.Status != "cancelled"
                    && b.Date >= today);
                var livePerms = await _db.PermanentBookings.CountAsync(p =>
                    p.VenueId == venue.Id
                    && p.PitchId != null && removedIds.Contains(p.PitchId)
                    && p.Status == "active");

                if (liveBookings + livePerms > 0)
                    return Conflict(new ApiResponse<object>
                    {
                        Success = false,
                        Message = $"Cannot remove that pitch: {liveBookings} upcoming booking(s) and " +
                                  $"{livePerms} standing reservation(s) still use it. Cancel or move them first."
                    });
            }

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

        if (!VenueAccess.CanManage(venue, UserId, UserRole))
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "You do not have permission to manage this venue" });

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
        OperatingHoursInfo? venueHours = AvailabilityHelper.ResolveHoursForDay(venue.OperatingHours, dayName);

        // Get existing bookings for that day (non-cancelled)
        var existingBookings = await _db.Bookings
            .Where(b => b.VenueId == venueId
                && b.Date.Date == bookingDate.Date
                && b.Status != "cancelled")
            .ToListAsync();

        // Active owner-managed permanents matching this date's weekday. We treat
        // them as virtual "always booked" rules — the conflict checker sees them
        // as additional booked slots without ever materialising into the bookings
        // table.
        var dow = (int)bookingDate.DayOfWeek;
        var activePermanents = StandingOccurrence.NotYetRecorded(
            await _db.PermanentBookings
                .Where(p => p.VenueId == venueId && p.Status == "active" && p.DayOfWeek == dow)
                .ToListAsync(),
            existingBookings);

        var pitches = PitchSizes.ResolvedPitches(venue);

        // Single-pitch request: build the response scoped to that pitch only and
        // keep the legacy top-level shape so old clients keep working.
        if (!string.IsNullOrEmpty(pitchId))
        {
            var pitch = pitches.FirstOrDefault(p => p.Id == pitchId);
            if (pitch == null)
                return NotFound(new ApiResponse<object> { Success = false, Message = "Pitch not found" });

            var resp = BuildAvailabilityForPitch(venue, pitch, existingBookings, activePermanents, venueHours, bookingDate);
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
                // Resolved from the pitch: `parent` is the venue's FOOTBALL size, so handing
                // it to a padel booking both mislabelled it and made it weigh 4 units, not 1.
                PitchSize = b.PitchSize ?? PitchSizes.ParentSizeForPitch(venue, b.PitchId),
                UnitWeight = PitchSizes.WeightOf(b.PitchSize ?? PitchSizes.ParentSizeForPitch(venue, b.PitchId))
            })
            .OrderBy(s => s.StartTime)
            .ToList();

        // Merge active permanents into the legacy flat list so old clients (that
        // ignore the per-pitch array) still see the slot blocked.
        legacyBookedSlots.AddRange(activePermanents
            .Where(p => !string.IsNullOrEmpty(p.StartTime))
            .Select(p => new BookedSlotInfo
            {
                StartTime = p.StartTime,
                Duration = p.Duration,
                Sport = null,
                PitchId = p.PitchId,
                PitchSize = p.PitchSize ?? PitchSizes.ParentSizeForPitch(venue, p.PitchId),
                UnitWeight = PitchSizes.WeightOf(p.PitchSize ?? PitchSizes.ParentSizeForPitch(venue, p.PitchId))
            }));
        legacyBookedSlots = legacyBookedSlots.OrderBy(s => s.StartTime).ToList();

        var legacyOffered = PitchSizes.OfferedSizesForSport(venue, "football");
        var legacyCapacity = PitchSizes.CapacityOfForSport(venue, "football");

        var perPitch = pitches
            .Select(p => BuildPitchAvailability(venue, p, existingBookings, activePermanents, venueHours, bookingDate))
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

    /// <summary>
    /// Build the single-pitch AvailableSlotsResponse (legacy shape, no <c>pitches</c> array).
    /// </summary>
    private static AvailableSlotsResponse BuildAvailabilityForPitch(
        Venue v, PitchDto pitch, List<Booking> allBookings,
        List<PermanentBooking> activePermanents,
        OperatingHoursInfo? venueHours, DateTime bookingDate)
    {
        // ResolveHoursForDay accepts both full and short day names.
        var dayName = bookingDate.DayOfWeek.ToString().ToLower();
        var hours = AvailabilityHelper.ResolvePitchHours(pitch, dayName, venueHours);

        var pitchBookings = allBookings
            .Where(b => AvailabilityHelper.MatchesPitch(b, v, pitch))
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
            .ToList();

        // Merge owner-managed permanents on this pitch + this weekday.
        booked.AddRange(activePermanents
            .Where(p => AvailabilityHelper.MatchesPitch(p, v, pitch))
            .Where(p => !string.IsNullOrEmpty(p.StartTime))
            .Select(p => new BookedSlotInfo
            {
                StartTime = p.StartTime,
                Duration = p.Duration,
                Sport = pitch.Sport,
                PitchId = pitch.Id,
                PitchSize = p.PitchSize ?? pitch.ParentSize,
                UnitWeight = PitchSizes.WeightOf(p.PitchSize ?? pitch.ParentSize)
            }));
        booked = booked.OrderBy(s => s.StartTime).ToList();

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
        List<PermanentBooking> activePermanents,
        OperatingHoursInfo? venueHours, DateTime bookingDate)
    {
        // ResolveHoursForDay accepts both full and short day names.
        var dayName = bookingDate.DayOfWeek.ToString().ToLower();
        var hours = AvailabilityHelper.ResolvePitchHours(pitch, dayName, venueHours);

        var pitchBookings = allBookings
            .Where(b => AvailabilityHelper.MatchesPitch(b, v, pitch))
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
            .ToList();

        // Merge active permanents that target this pitch + this weekday.
        pitchBookings.AddRange(activePermanents
            .Where(p => AvailabilityHelper.MatchesPitch(p, v, pitch))
            .Where(p => !string.IsNullOrEmpty(p.StartTime))
            .Select(p => new BookedSlotInfo
            {
                StartTime = p.StartTime,
                Duration = p.Duration,
                Sport = pitch.Sport,
                PitchId = pitch.Id,
                PitchSize = p.PitchSize ?? pitch.ParentSize,
                UnitWeight = PitchSizes.WeightOf(p.PitchSize ?? pitch.ParentSize)
            }));
        pitchBookings = pitchBookings.OrderBy(s => s.StartTime).ToList();

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

    [HttpGet("{venueId}/stats")]
    public async Task<IActionResult> Stats(string venueId)
    {
        var venue = await _db.Venues.FindAsync(venueId);
        if (venue == null)
            return NotFound(new ApiResponse<object> { Success = false, Message = "Venue not found" });

        // Stats include revenue — only the venue's owner or an admin may read them.
        if (!VenueAccess.CanManage(venue, UserId, UserRole))
            return StatusCode(403, new ApiResponse<object> { Success = false, Message = "You do not have permission to view this venue's stats" });

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
