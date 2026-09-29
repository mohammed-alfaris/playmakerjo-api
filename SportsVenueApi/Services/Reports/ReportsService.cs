using Microsoft.EntityFrameworkCore;
using SportsVenueApi.Constants;
using SportsVenueApi.Data;
using SportsVenueApi.DTOs.Reports;
using SportsVenueApi.DTOs.Venues;
using SportsVenueApi.Helpers;
using SportsVenueApi.Models;

namespace SportsVenueApi.Services.Reports;

/// <summary>
/// The owner's and the platform's reports. Every number is defined once, here, and shared by
/// the screen and the Excel export:
///
/// - <b>Collected</b>: money in the payments ledger whose Amman date falls in the period. The
///   ledger is the only record of cash actually taken; a booking's status is not.
/// - <b>Booked</b>: total price of non-cancelled bookings played in the period.
/// - <b>Outstanding</b>: counter bookings already played and not fully paid, right now — the
///   same rule as a customer's "owes" on the customer pages, so the two always agree.
/// - <b>Platform fee</b>: the fee on app bookings attended in the period.
/// - <b>Occupancy</b>: booked pitch-time ÷ open pitch-time, with a half booked on a
///   subdividable pitch counting as half.
///
/// Rows are loaded narrowly (scope + period, projected, no tracking) and aggregated in memory:
/// a venue has tens of bookings a day and a period is capped at a year.
/// </summary>
public class ReportsService
{
    private readonly AppDbContext _db;

    public ReportsService(AppDbContext db) => _db = db;

    private const double Eps = 0.0005;

    // ── Loading ──────────────────────────────────────────────────────────────

    private sealed record BookingRow(
        string Id, string VenueId, string? PitchId, string? PitchSize, string? Sport,
        DateTime Date, string? StartTime, int Duration, string Status,
        double TotalAmount, double AmountPaid, double SystemFee, bool IsManual,
        string? PermanentBookingId, string? RecurringGroupId, DateTime CreatedAt,
        DateTime? AutoCancelledAt, string? CustomerId, string PlayerId);

    private sealed record PaymentRow(
        double Amount, string? Method, string Kind, DateTime Date,
        string? RecordedByUserId, string? CustomerId, string VenueId, string? PitchId, string? Sport);

    private IQueryable<Booking> ScopedBookings(ReportScope scope)
    {
        var q = _db.Bookings.AsNoTracking();
        if (!scope.PlatformWide) q = q.Where(b => scope.VenueIds!.Contains(b.VenueId));
        return q;
    }

    private Task<List<BookingRow>> LoadBookingsAsync(ReportScope scope, ReportPeriod period)
    {
        var from = period.From;
        var toExclusive = period.To.AddDays(1);
        return ScopedBookings(scope)
            .Where(b => b.Date >= from && b.Date < toExclusive)
            .Select(b => new BookingRow(
                b.Id, b.VenueId, b.PitchId, b.PitchSize, b.Sport,
                b.Date, b.StartTime, b.Duration, b.Status,
                b.TotalAmount, b.AmountPaid, b.SystemFee, b.IsManual,
                b.PermanentBookingId, b.RecurringGroupId, b.CreatedAt,
                b.AutoCancelledAt, b.CustomerId, b.PlayerId))
            .ToListAsync();
    }

    private Task<List<PaymentRow>> LoadPaymentsAsync(ReportScope scope, ReportPeriod period)
    {
        var fromUtc = period.FromUtc;
        var toUtc = period.ToUtcExclusive;
        // Scoped through the booking: payment.venue_id is null on rows older than the column.
        var q = _db.Payments.AsNoTracking().Where(p => p.Date >= fromUtc && p.Date < toUtc);
        if (!scope.PlatformWide) q = q.Where(p => scope.VenueIds!.Contains(p.Booking.VenueId));
        return q.Select(p => new PaymentRow(
                p.Amount, p.Method, p.Kind, p.Date, p.RecordedByUserId, p.CustomerId,
                p.Booking.VenueId, p.Booking.PitchId, p.Booking.Sport))
            .ToListAsync();
    }

    private Task<List<Venue>> LoadVenuesAsync(ReportScope scope)
    {
        var q = _db.Venues.AsNoTracking();
        if (!scope.PlatformWide) q = q.Where(v => scope.VenueIds!.Contains(v.Id));
        return q.ToListAsync();
    }

    // ── Shared definitions ───────────────────────────────────────────────────

    private static double R(double v) => Math.Round(v, 3);
    private static double Pct(double part, double whole) => whole <= 0 ? 0 : Math.Round(part * 100.0 / whole, 1);

    private static bool Live(BookingRow b) => b.Status != "cancelled";

    private static bool Attended(BookingRow b, DateTime today) => Helpers.Attendance.Attended(b.Date, b.Status, today);

    /// <summary>cash, cliq, or other (card, and anything a future method adds).</summary>
    public static string MethodKey(string? method) => method switch
    {
        "cash" => "cash",
        "cliq" => "cliq",
        _ => "other",
    };

    /// <summary>
    /// Where a booking came from. A recorded standing week and a series occurrence are
    /// checked first: they are "manual" or "app" too, but that is not the story they tell.
    /// </summary>
    public static string Channel(bool isManual, string? permanentBookingId, string? recurringGroupId) =>
        permanentBookingId != null ? "weekly"
        : recurringGroupId != null ? "series"
        : isManual ? "counter"
        : "app";

    private static string Channel(BookingRow b) => Channel(b.IsManual, b.PermanentBookingId, b.RecurringGroupId);

    /// <summary>Days between booking and playing, in Amman days.</summary>
    public static string LeadBucket(DateTime playDate, DateTime createdAtUtc)
    {
        var days = (playDate.Date - ReportPeriod.AmmanDateOf(createdAtUtc)).TotalDays;
        return days <= 0 ? "same_day" : days <= 2 ? "1_2_days" : days <= 7 ? "3_7_days" : "8_plus_days";
    }

    private static PeriodInfo Info(ReportPeriod p, bool compare)
    {
        var prev = compare ? p.Previous() : null;
        return new PeriodInfo(D(p.From), D(p.To), p.Days, prev == null ? null : D(prev.From), prev == null ? null : D(prev.To));
    }

    private static string D(DateTime d) => d.ToString("yyyy-MM-dd");

    private static Kpi K(double value, double? previous) => new(R(value), previous == null ? null : R(previous.Value));

    // ── Money ────────────────────────────────────────────────────────────────

    private sealed record MoneyTotals(double Collected, double Booked, double Fee);

    private async Task<MoneyTotals> MoneyTotalsAsync(ReportScope scope, ReportPeriod period)
    {
        var today = PlatformConstants.JordanToday();
        var payments = await LoadPaymentsAsync(scope, period);
        var bookings = await LoadBookingsAsync(scope, period);
        return new MoneyTotals(
            payments.Sum(p => p.Amount),
            bookings.Where(Live).Sum(b => b.TotalAmount),
            bookings.Where(b => !b.IsManual && Attended(b, today)).Sum(b => b.SystemFee));
    }

    public async Task<MoneyReport> MoneyAsync(ReportScope scope, ReportPeriod period, bool compare)
    {
        var today = PlatformConstants.JordanToday();
        var payments = await LoadPaymentsAsync(scope, period);
        var bookings = await LoadBookingsAsync(scope, period);
        var live = bookings.Where(Live).ToList();
        var prev = compare ? await MoneyTotalsAsync(scope, period.Previous()) : null;

        var collected = payments.Sum(p => p.Amount);
        var booked = live.Sum(b => b.TotalAmount);
        var fee = bookings.Where(b => !b.IsManual && Attended(b, today)).Sum(b => b.SystemFee);

        var byMethod = payments
            .GroupBy(p => MethodKey(p.Method))
            .Select(g => new KeyAmount(g.Key, R(g.Sum(p => p.Amount)), g.Count()))
            .OrderByDescending(x => x.Amount).ToList();
        var byKind = payments
            .GroupBy(p => p.Kind)
            .Select(g => new KeyAmount(g.Key, R(g.Sum(p => p.Amount)), g.Count()))
            .OrderByDescending(x => x.Amount).ToList();

        var payByDay = payments.ToLookup(p => ReportPeriod.AmmanDateOf(p.Date));
        var bookedByDay = live.ToLookup(b => b.Date.Date);
        var daily = period.Dates().Select(d =>
        {
            var ps = payByDay[d].ToList();
            return new MoneyDay(D(d),
                R(ps.Where(p => MethodKey(p.Method) == "cash").Sum(p => p.Amount)),
                R(ps.Where(p => MethodKey(p.Method) == "cliq").Sum(p => p.Amount)),
                R(ps.Where(p => MethodKey(p.Method) == "other").Sum(p => p.Amount)),
                R(bookedByDay[d].Sum(b => b.TotalAmount)));
        }).ToList();

        var venueIds = live.Select(b => b.VenueId).Concat(payments.Select(p => p.VenueId)).Distinct().ToList();
        var venues = await _db.Venues.AsNoTracking().Where(v => venueIds.Contains(v.Id)).ToListAsync();
        var venueById = venues.ToDictionary(v => v.Id);

        var byVenue = venueIds.Where(venueById.ContainsKey).Select(id =>
        {
            var v = venueById[id];
            var vb = live.Where(b => b.VenueId == id).ToList();
            return new VenueMoney(id, v.Name, v.NameAr,
                R(payments.Where(p => p.VenueId == id).Sum(p => p.Amount)),
                R(vb.Sum(b => b.TotalAmount)), vb.Count);
        }).OrderByDescending(x => x.Collected).ThenByDescending(x => x.Booked).ToList();

        var byPitch = live
            .Where(b => venueById.ContainsKey(b.VenueId))
            .GroupBy(b => (b.VenueId, Pitch: ResolvePitch(venueById[b.VenueId], b.PitchId, b.Sport)))
            .Where(g => g.Key.Pitch != null)
            .Select(g => new PitchMoney(g.Key.VenueId, venueById[g.Key.VenueId].Name,
                g.Key.Pitch!.Id, g.Key.Pitch.Name, g.Key.Pitch.NameAr,
                R(g.Sum(b => b.TotalAmount)), g.Count()))
            .OrderByDescending(x => x.Booked).ToList();

        var (outstanding, outstandingCount, items) = await OutstandingAsync(scope);

        return new MoneyReport(
            Info(period, compare),
            K(collected, prev?.Collected),
            K(booked, prev?.Booked),
            R(outstanding), outstandingCount,
            scope.IsAdmin ? K(fee, prev?.Fee) : null,
            scope.IsAdmin ? K(booked - fee, prev == null ? null : prev.Booked - prev.Fee) : null,
            byMethod, byKind, daily, byVenue, byPitch, items);
    }

    /// <summary>The pitch a booking sits on; legacy rows with no pitch go to the first pitch of their sport.</summary>
    private static PitchDto? ResolvePitch(Venue v, string? pitchId, string? sport)
    {
        var pitches = PitchSizes.ResolvedPitches(v);
        if (!string.IsNullOrEmpty(pitchId))
            return pitches.FirstOrDefault(p => p.Id == pitchId);
        return pitches.FirstOrDefault(p => string.Equals(p.Sport, sport, StringComparison.OrdinalIgnoreCase))
            ?? pitches.FirstOrDefault();
    }

    /// <summary>
    /// Owed right now: counter bookings already played and not fully paid. App bookings are
    /// excluded — payment there is the platform's flow, not credit the owner chose to give.
    /// Mirrors CustomersController's AmountOwed so the report and the customer pages agree.
    /// </summary>
    private async Task<(double Total, int Count, List<OutstandingItem> Items)> OutstandingAsync(ReportScope scope)
    {
        var today = PlatformConstants.JordanToday();
        var rows = await ScopedBookings(scope)
            .Where(b => b.IsManual && b.Status != "cancelled")
            .Where(Helpers.Attendance.AttendedExpr(today))
            .Where(b => b.AmountPaid + 0.001 < b.TotalAmount)
            .OrderByDescending(b => b.Date)
            .Select(b => new
            {
                b.Id, b.Date, b.StartTime, VenueName = b.Venue.Name,
                b.CustomerId, CustomerName = b.Customer != null ? b.Customer.Name : null,
                CustomerPhone = b.Customer != null ? b.Customer.Phone : null,
                b.TotalAmount, b.AmountPaid,
            })
            .ToListAsync();

        var items = rows.Take(200).Select(r => new OutstandingItem(
            r.Id, D(r.Date), r.StartTime, r.VenueName,
            scope.CanSeeCustomers ? r.CustomerId : null,
            scope.CanSeeCustomers ? r.CustomerName : null,
            scope.CanSeeCustomers ? r.CustomerPhone : null,
            R(r.TotalAmount), R(r.AmountPaid), R(r.TotalAmount - r.AmountPaid))).ToList();

        return (rows.Sum(r => r.TotalAmount - r.AmountPaid), rows.Count, items);
    }

    // ── Bookings health ──────────────────────────────────────────────────────

    private sealed record HealthTotals(int Bookings, double CancelRate, double NoShowRate);

    private static HealthTotals Health(List<BookingRow> all, DateTime today)
    {
        var attended = all.Count(b => Attended(b, today));
        var noShows = all.Count(b => b.Status == "no_show");
        return new HealthTotals(
            all.Count(Live),
            Pct(all.Count(b => b.Status == "cancelled"), all.Count),
            Pct(noShows, attended + noShows));
    }

    public async Task<BookingsReport> BookingsAsync(ReportScope scope, ReportPeriod period, bool compare)
    {
        var today = PlatformConstants.JordanToday();
        var all = await LoadBookingsAsync(scope, period);
        var live = all.Where(Live).ToList();
        var cancelled = all.Where(b => b.Status == "cancelled").ToList();
        var now = Health(all, today);
        var prev = compare ? Health(await LoadBookingsAsync(scope, period.Previous()), today) : null;

        var byStatus = all.GroupBy(b => b.Status)
            .Select(g => new KeyCount(g.Key, g.Count())).OrderByDescending(x => x.Count).ToList();
        var byChannel = live.GroupBy(Channel)
            .Select(g => new KeyCount(g.Key, g.Count())).OrderByDescending(x => x.Count).ToList();

        var liveByDay = live.ToLookup(b => b.Date.Date);
        var cancelledByDay = cancelled.ToLookup(b => b.Date.Date);
        var daily = period.Dates().Select(d =>
        {
            var day = liveByDay[d].ToList();
            return new BookingsDay(D(d),
                day.Count(b => Channel(b) == "app"),
                day.Count(b => Channel(b) == "counter"),
                day.Count(b => Channel(b) == "weekly"),
                day.Count(b => Channel(b) == "series"),
                cancelledByDay[d].Count());
        }).ToList();

        // Lead time only means something for bookings someone made on purpose: a recorded
        // standing week or a series occurrence is created on a schedule, not by a customer.
        var leadOrder = new[] { "same_day", "1_2_days", "3_7_days", "8_plus_days" };
        var lead = live.Where(b => Channel(b) is "app" or "counter")
            .GroupBy(b => LeadBucket(b.Date, b.CreatedAt))
            .ToDictionary(g => g.Key, g => g.Count());
        var leadTime = leadOrder.Select(k => new KeyCount(k, lead.GetValueOrDefault(k))).ToList();

        var sports = live.GroupBy(b => (b.Sport ?? "other").ToLowerInvariant())
            .Select(g => new KeyCount(g.Key, g.Count())).OrderByDescending(x => x.Count).ToList();

        return new BookingsReport(
            Info(period, compare),
            K(now.Bookings, prev?.Bookings),
            K(now.CancelRate, prev?.CancelRate),
            K(now.NoShowRate, prev?.NoShowRate),
            all.Count(b => Attended(b, today)),
            all.Count(b => b.Status == "no_show"),
            cancelled.Count(b => b.AutoCancelledAt == null),
            cancelled.Count(b => b.AutoCancelledAt != null),
            byStatus, byChannel, daily, leadTime, sports);
    }

    // ── Busy hours ───────────────────────────────────────────────────────────

    private sealed class OccupancyAccumulator
    {
        public readonly double[,] Open = new double[7, 24];
        public readonly double[,] Booked = new double[7, 24];
        public readonly Dictionary<(string VenueId, string PitchId), (double Open, double Booked)> Pitches = new();

        public double TotalOpen, TotalBooked;
    }

    /// <summary>
    /// Adds [startMin, endMin) — minutes from the operating date's midnight, possibly past 24h
    /// for a late-night window — into the weekday × hour cells, weighted by <paramref name="factor"/>.
    /// Minutes past midnight land on the next weekday, where they really happened.
    /// </summary>
    private static double Spread(double[,] cells, int dow, double startMin, double endMin, double factor)
    {
        double added = 0;
        for (var h = (int)Math.Floor(startMin / 60); h * 60 < endMin; h++)
        {
            var overlap = Math.Min(endMin, (h + 1) * 60) - Math.Max(startMin, h * 60);
            if (overlap <= 0) continue;
            cells[(dow + h / 24) % 7, h % 24] += overlap * factor;
            added += overlap * factor;
        }
        return added;
    }

    private static bool TryMinutes(string? hhmm, out double minutes)
    {
        minutes = 0;
        if (string.IsNullOrEmpty(hhmm) || !TimeSpan.TryParse(hhmm, out var t)) return false;
        minutes = t.TotalMinutes;
        return true;
    }

    private static bool StandingAppliesOn(PermanentBooking p, DateTime date) =>
        ReportPeriod.AmmanDateOf(p.CreatedAt) <= date
        && (p.Status == "active" || (p.CancelledAt != null && ReportPeriod.AmmanDateOf(p.CancelledAt.Value) > date));

    private async Task<OccupancyAccumulator> AccumulateOccupancyAsync(ReportScope scope, ReportPeriod period)
    {
        var acc = new OccupancyAccumulator();
        var venues = await LoadVenuesAsync(scope);
        if (venues.Count == 0) return acc;

        var venueIds = venues.Select(v => v.Id).ToList();
        var from = period.From;
        var toExclusive = period.To.AddDays(1);
        var bookings = await _db.Bookings.AsNoTracking()
            .Where(b => venueIds.Contains(b.VenueId) && b.Date >= from && b.Date < toExclusive && b.Status != "cancelled")
            .ToListAsync();
        var standing = await _db.PermanentBookings.AsNoTracking()
            .Where(p => venueIds.Contains(p.VenueId))
            .ToListAsync();

        var bookingsByVenueDay = bookings.ToLookup(b => (b.VenueId, b.Date.Date));
        var standingByVenue = standing.ToLookup(p => p.VenueId);
        var hoursCache = new Dictionary<(string, string, string), DayHoursResult>();

        foreach (var v in venues)
        {
            var pitches = PitchSizes.ResolvedPitches(v);
            foreach (var date in period.Dates())
            {
                var dow = (int)date.DayOfWeek;
                var dayName = date.DayOfWeek.ToString().ToLower();
                var dayBookings = bookingsByVenueDay[(v.Id, date)].ToList();
                var dayStanding = StandingOccurrence.NotYetRecorded(
                    standingByVenue[v.Id].Where(p => p.DayOfWeek == dow && StandingAppliesOn(p, date)),
                    dayBookings);

                foreach (var pitch in pitches)
                {
                    var key = (v.Id, pitch.Id, dayName);
                    if (!hoursCache.TryGetValue(key, out var hours))
                        hoursCache[key] = hours = AvailabilityHelper.ResolveEffectiveDayHours(v, pitch, dayName);
                    if (hours.Status != DayHoursStatus.Open || hours.Window == null) continue;
                    if (!TryMinutes(hours.Window.Open, out var open) || !TryMinutes(hours.Window.Close, out var close)) continue;
                    if (close <= open) close += 24 * 60; // 18:00–02:00 runs into the next day

                    var openMin = Spread(acc.Open, dow, open, close, 1);

                    var capacity = PitchSizes.CapacityOf(pitch);
                    var subdividable = pitch.ParentSize != null && (pitch.SubSizes?.Count ?? 0) > 0;
                    double Share(string? size) =>
                        subdividable ? Math.Min(1.0, PitchSizes.WeightOf(size ?? pitch.ParentSize) / (double)capacity) : 1.0;

                    // Per-pitch booked minutes go into a scratch grid first so that a
                    // double-booked slot cannot push the pitch past 100% of what was open.
                    var scratch = new double[7, 24];
                    foreach (var b in dayBookings.Where(b => AvailabilityHelper.MatchesPitch(b, v, pitch)))
                    {
                        if (!TryMinutes(b.StartTime, out var s)) continue;
                        if (s < open && s + 24 * 60 < close) s += 24 * 60; // a 01:00 slot on a late window
                        Spread(scratch, dow, Math.Max(s, open), Math.Min(s + b.Duration, close), Share(b.PitchSize));
                    }
                    foreach (var p in dayStanding.Where(p => AvailabilityHelper.MatchesPitch(p, v, pitch)))
                    {
                        if (!TryMinutes(p.StartTime, out var s)) continue;
                        if (s < open && s + 24 * 60 < close) s += 24 * 60;
                        Spread(scratch, dow, Math.Max(s, open), Math.Min(s + p.Duration, close), Share(p.PitchSize));
                    }

                    var pitchOpen = new double[7, 24];
                    Spread(pitchOpen, dow, open, close, 1);
                    double bookedMin = 0;
                    for (var d = 0; d < 7; d++)
                    for (var h = 0; h < 24; h++)
                    {
                        var m = Math.Min(scratch[d, h], pitchOpen[d, h]);
                        acc.Booked[d, h] += m;
                        bookedMin += m;
                    }

                    acc.TotalOpen += openMin;
                    acc.TotalBooked += bookedMin;
                    var pk = (v.Id, pitch.Id);
                    var cur = acc.Pitches.GetValueOrDefault(pk);
                    acc.Pitches[pk] = (cur.Open + openMin, cur.Booked + bookedMin);
                }
            }
        }
        return acc;
    }

    private static double? CellPct(double open, double booked) => open <= 0 ? null : Math.Round(booked * 100.0 / open, 1);

    public async Task<OccupancyReport> OccupancyAsync(ReportScope scope, ReportPeriod period, bool compare)
    {
        var acc = await AccumulateOccupancyAsync(scope, period);
        double? prevPct = null;
        if (compare)
        {
            var prev = await AccumulateOccupancyAsync(scope, period.Previous());
            prevPct = Pct(prev.TotalBooked, prev.TotalOpen);
        }

        var grid = new List<OccupancyCell>();
        for (var d = 0; d < 7; d++)
        for (var h = 0; h < 24; h++)
            grid.Add(new OccupancyCell(d, h, Math.Round(acc.Open[d, h] / 60, 2), Math.Round(acc.Booked[d, h] / 60, 2),
                CellPct(acc.Open[d, h], acc.Booked[d, h])));

        var venues = await LoadVenuesAsync(scope);
        var venueById = venues.ToDictionary(v => v.Id);
        var byPitch = acc.Pitches.Select(kv =>
        {
            var v = venueById[kv.Key.VenueId];
            var pitch = PitchSizes.ResolvedPitches(v).First(p => p.Id == kv.Key.PitchId);
            return new PitchOccupancy(v.Id, v.Name, pitch.Id, pitch.Name, pitch.NameAr,
                Math.Round(kv.Value.Open / 60, 2), Math.Round(kv.Value.Booked / 60, 2),
                CellPct(kv.Value.Open, kv.Value.Booked));
        }).OrderByDescending(x => x.Pct ?? -1).ToList();

        var openCells = grid.Where(c => c.Pct != null).ToList();
        return new OccupancyReport(
            Info(period, compare),
            new Kpi(Pct(acc.TotalBooked, acc.TotalOpen), prevPct),
            Math.Round(acc.TotalOpen / 60, 2),
            Math.Round(acc.TotalBooked / 60, 2),
            grid,
            byPitch,
            openCells.OrderByDescending(c => c.Pct).ThenByDescending(c => c.BookedHours).Take(5).ToList(),
            openCells.OrderBy(c => c.Pct).ThenByDescending(c => c.OpenHours).Take(5).ToList());
    }

    // ── Customers & team ─────────────────────────────────────────────────────

    private sealed record CustomerTotals(int Active, int New);

    private static CustomerTotals CustomerCounts(List<(string CustomerId, DateTime Date)> lifetime, ReportPeriod period)
    {
        var first = lifetime.GroupBy(x => x.CustomerId).ToDictionary(g => g.Key, g => g.Min(x => x.Date.Date));
        var active = lifetime.Where(x => period.Contains(x.Date)).Select(x => x.CustomerId).ToHashSet();
        return new CustomerTotals(active.Count, active.Count(id => period.Contains(first[id])));
    }

    public async Task<CustomersReport> CustomersAsync(ReportScope scope, ReportPeriod period, bool compare)
    {
        return new CustomersReport(
            Info(period, compare),
            scope.CanSeeCustomers ? await CustomersSectionAsync(scope, period, compare) : null,
            scope.CanSeeTeam ? await TeamAsync(scope, period) : null);
    }

    private async Task<CustomersSection> CustomersSectionAsync(ReportScope scope, ReportPeriod period, bool compare)
    {
        var today = PlatformConstants.JordanToday();
        // Lifetime rows: "new" means the first-ever booking falls in the period, and "gone
        // quiet" looks back past it — both need history, not just the period.
        var lifetime = await ScopedBookings(scope)
            .Where(b => b.CustomerId != null && b.Status != "cancelled")
            .Select(b => new { CustomerId = b.CustomerId!, b.Date, b.Status })
            .ToListAsync();
        var dated = lifetime.Select(x => (x.CustomerId, x.Date)).ToList();

        var now = CustomerCounts(dated, period);
        var prev = compare ? CustomerCounts(dated, period.Previous()) : null;

        var payments = await LoadPaymentsAsync(scope, period);
        var paidBy = payments.Where(p => p.CustomerId != null)
            .GroupBy(p => p.CustomerId!).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

        var visits = lifetime
            .Where(x => period.Contains(x.Date) && Helpers.Attendance.Attended(x.Date, x.Status, today))
            .GroupBy(x => x.CustomerId).ToDictionary(g => g.Key, g => g.Count());

        // Same rule as the customer pages: two or more visits, the last over 30 days ago.
        var lapsedIds = lifetime.GroupBy(x => x.CustomerId).Where(g =>
        {
            var attended = g.Count(x => Helpers.Attendance.Attended(x.Date, x.Status, today));
            var past = g.Where(x => x.Date.Date < today).ToList();
            return attended >= 2 && past.Count > 0 && (today - past.Max(x => x.Date.Date)).TotalDays > 30;
        }).Select(g => g.Key).ToList();

        var ids = visits.Keys.Concat(paidBy.Keys).Concat(lapsedIds).Distinct().ToList();
        var customers = await _db.Customers.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Phone, c.Status })
            .ToDictionaryAsync(c => c.Id);

        TopCustomer Top(string id) => new(id, customers[id].Name, customers[id].Phone,
            visits.GetValueOrDefault(id), R(paidBy.GetValueOrDefault(id)));

        var topByVisits = visits.Where(kv => customers.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value).ThenByDescending(kv => paidBy.GetValueOrDefault(kv.Key))
            .Take(10).Select(kv => Top(kv.Key)).ToList();
        var topBySpend = paidBy.Where(kv => customers.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value).Take(10).Select(kv => Top(kv.Key)).ToList();

        var returning = now.Active - now.New;
        return new CustomersSection(
            new Kpi(now.Active, prev?.Active),
            new Kpi(now.New, prev?.New),
            returning,
            Pct(returning, now.Active),
            lapsedIds.Count(id => customers.TryGetValue(id, out var c) && c.Status == "active"),
            topByVisits,
            topBySpend);
    }

    /// <summary>
    /// Who took which money — the end-of-shift drawer check. Payments with no recorder were
    /// taken by the app itself.
    /// </summary>
    private async Task<List<TeamMember>> TeamAsync(ReportScope scope, ReportPeriod period)
    {
        var payments = await LoadPaymentsAsync(scope, period);
        var fromUtc = period.FromUtc;
        var toUtc = period.ToUtcExclusive;
        // A counter booking's player_id is whoever keyed it in; recorded standing weeks are
        // created on the owner's behalf by the schedule, not taken at the counter.
        var counter = await ScopedBookings(scope)
            .Where(b => b.IsManual && b.PermanentBookingId == null && b.CreatedAt >= fromUtc && b.CreatedAt < toUtc)
            .GroupBy(b => b.PlayerId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        var userIds = payments.Where(p => p.RecordedByUserId != null).Select(p => p.RecordedByUserId!)
            .Concat(counter.Keys).Distinct().ToList();
        var users = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Name, u.Role })
            .ToDictionaryAsync(u => u.Id);

        var members = userIds.Where(users.ContainsKey).Select(id =>
        {
            var mine = payments.Where(p => p.RecordedByUserId == id).ToList();
            return new TeamMember(id, users[id].Name, users[id].Role, mine.Count, R(mine.Sum(p => p.Amount)),
                R(mine.Where(p => MethodKey(p.Method) == "cash").Sum(p => p.Amount)),
                R(mine.Where(p => MethodKey(p.Method) == "cliq").Sum(p => p.Amount)),
                counter.GetValueOrDefault(id));
        }).ToList();

        var viaApp = payments.Where(p => p.RecordedByUserId == null).ToList();
        if (viaApp.Count > 0)
            members.Add(new TeamMember(null, "app", "app", viaApp.Count, R(viaApp.Sum(p => p.Amount)),
                R(viaApp.Where(p => MethodKey(p.Method) == "cash").Sum(p => p.Amount)),
                R(viaApp.Where(p => MethodKey(p.Method) == "cliq").Sum(p => p.Amount)), 0));

        return members.OrderByDescending(m => m.Collected).ThenByDescending(m => m.CounterBookings).ToList();
    }

    // ── Platform (admin) ─────────────────────────────────────────────────────

    private sealed record PlatformTotals(
        double Booked, double Fee, double Collected, int Bookings, double AppShare,
        int NewCompanies, int NewVenues, int NewPlayers, int ActiveCompanies);

    private async Task<PlatformTotals> PlatformTotalsAsync(ReportPeriod period)
    {
        var all = ReportScope.Denied with { Allowed = true, IsAdmin = true };
        var today = PlatformConstants.JordanToday();
        var bookings = await LoadBookingsAsync(all, period);
        var live = bookings.Where(Live).ToList();
        var payments = await LoadPaymentsAsync(all, period);
        var fromUtc = period.FromUtc;
        var toUtc = period.ToUtcExclusive;

        var activeVenueIds = live.Select(b => b.VenueId).Distinct().ToList();
        var activeCompanies = await _db.Venues.AsNoTracking()
            .Where(v => activeVenueIds.Contains(v.Id)).Select(v => v.OwnerId).Distinct().CountAsync();

        return new PlatformTotals(
            live.Sum(b => b.TotalAmount),
            bookings.Where(b => !b.IsManual && Attended(b, today)).Sum(b => b.SystemFee),
            payments.Sum(p => p.Amount),
            live.Count,
            Pct(live.Count(b => !b.IsManual), live.Count),
            await _db.Users.CountAsync(u => u.Role == "venue_owner" && u.CreatedAt >= fromUtc && u.CreatedAt < toUtc),
            await _db.Venues.CountAsync(v => v.CreatedAt >= fromUtc && v.CreatedAt < toUtc),
            await _db.Users.CountAsync(u => u.Role == "player" && u.CreatedAt >= fromUtc && u.CreatedAt < toUtc),
            activeCompanies);
    }

    public async Task<PlatformReport> PlatformAsync(ReportPeriod period, bool compare)
    {
        var all = ReportScope.Denied with { Allowed = true, IsAdmin = true };
        var today = PlatformConstants.JordanToday();
        var now = await PlatformTotalsAsync(period);
        var prev = compare ? await PlatformTotalsAsync(period.Previous()) : null;

        var bookings = await LoadBookingsAsync(all, period);
        var live = bookings.Where(Live).ToList();
        var payments = await LoadPaymentsAsync(all, period);

        double FeeOf(IEnumerable<BookingRow> rows) => rows.Where(b => !b.IsManual && Attended(b, today)).Sum(b => b.SystemFee);

        var byDay = bookings.ToLookup(b => b.Date.Date);
        var daily = period.Dates().Select(d =>
        {
            var day = byDay[d].ToList();
            var dayLive = day.Where(Live).ToList();
            return new PlatformDay(D(d), R(dayLive.Sum(b => b.TotalAmount)), R(FeeOf(day)), dayLive.Count);
        }).ToList();

        // Every company, active or not — an owner who stopped taking bookings is exactly the
        // row an admin needs to see.
        var owners = await _db.Users.AsNoTracking()
            .Where(u => u.Role == "venue_owner")
            .Select(u => new { u.Id, u.Name, u.Status })
            .ToListAsync();
        var companies = await _db.Companies.AsNoTracking()
            .Select(c => new { c.OwnerId, c.Name, c.NameAr })
            .ToDictionaryAsync(c => c.OwnerId);
        var venueOwner = await _db.Venues.AsNoTracking()
            .Select(v => new { v.Id, v.OwnerId })
            .ToDictionaryAsync(v => v.Id, v => v.OwnerId);

        var bookingsByOwner = bookings.Where(b => venueOwner.ContainsKey(b.VenueId)).ToLookup(b => venueOwner[b.VenueId]);
        var paymentsByOwner = payments.Where(p => venueOwner.ContainsKey(p.VenueId)).ToLookup(p => venueOwner[p.VenueId]);
        var venuesPerOwner = venueOwner.Values.GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());

        var rows = owners.Select(o =>
        {
            var ob = bookingsByOwner[o.Id].ToList();
            var obLive = ob.Where(Live).ToList();
            var company = companies.GetValueOrDefault(o.Id);
            return new CompanyRow(o.Id, company?.Name ?? o.Name, company?.NameAr, o.Status,
                venuesPerOwner.GetValueOrDefault(o.Id), obLive.Count,
                R(obLive.Sum(b => b.TotalAmount)), R(FeeOf(ob)),
                R(paymentsByOwner[o.Id].Sum(p => p.Amount)));
        }).OrderByDescending(r => r.Booked).ThenByDescending(r => r.Collected).ThenBy(r => r.Name).ToList();

        return new PlatformReport(
            Info(period, compare),
            K(now.Booked, prev?.Booked),
            K(now.Fee, prev?.Fee),
            K(now.Collected, prev?.Collected),
            new Kpi(now.Bookings, prev?.Bookings),
            new Kpi(now.AppShare, prev?.AppShare),
            new Kpi(now.NewCompanies, prev?.NewCompanies),
            new Kpi(now.NewVenues, prev?.NewVenues),
            new Kpi(now.NewPlayers, prev?.NewPlayers),
            new Kpi(now.ActiveCompanies, prev?.ActiveCompanies),
            daily,
            rows);
    }
}
