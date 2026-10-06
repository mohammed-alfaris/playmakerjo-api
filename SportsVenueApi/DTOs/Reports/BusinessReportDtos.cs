using System.Text.Json.Serialization;

namespace SportsVenueApi.DTOs.Reports;

// The reports built on ReportsService. Money is JOD, rounded to 3 decimals (fils). Dates are
// Amman calendar dates as "yyyy-MM-dd".

/// <summary>A headline number, with the previous period's value when compare was asked for.</summary>
public sealed record Kpi(
    [property: JsonPropertyName("value")] double Value,
    [property: JsonPropertyName("previous")] double? Previous);

public sealed record PeriodInfo(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("days")] int Days,
    [property: JsonPropertyName("previousFrom")] string? PreviousFrom,
    [property: JsonPropertyName("previousTo")] string? PreviousTo);

public sealed record KeyAmount(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("count")] int Count);

public sealed record KeyCount(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("count")] int Count);

// ── Money ─────────────────────────────────────────────────────────────────────

public sealed record MoneyDay(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("cash")] double Cash,
    [property: JsonPropertyName("cliq")] double Cliq,
    [property: JsonPropertyName("other")] double Other,
    [property: JsonPropertyName("booked")] double Booked);

public sealed record VenueMoney(
    [property: JsonPropertyName("venueId")] string VenueId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nameAr")] string? NameAr,
    [property: JsonPropertyName("collected")] double Collected,
    [property: JsonPropertyName("booked")] double Booked,
    [property: JsonPropertyName("bookings")] int Bookings);

public sealed record PitchMoney(
    [property: JsonPropertyName("venueId")] string VenueId,
    [property: JsonPropertyName("venueName")] string VenueName,
    [property: JsonPropertyName("pitchId")] string PitchId,
    [property: JsonPropertyName("pitchName")] string PitchName,
    [property: JsonPropertyName("pitchNameAr")] string? PitchNameAr,
    [property: JsonPropertyName("booked")] double Booked,
    [property: JsonPropertyName("bookings")] int Bookings);

public sealed record OutstandingItem(
    [property: JsonPropertyName("bookingId")] string BookingId,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("startTime")] string? StartTime,
    [property: JsonPropertyName("venueName")] string VenueName,
    [property: JsonPropertyName("customerId")] string? CustomerId,
    [property: JsonPropertyName("customerName")] string? CustomerName,
    [property: JsonPropertyName("customerPhone")] string? CustomerPhone,
    [property: JsonPropertyName("total")] double Total,
    [property: JsonPropertyName("paid")] double Paid,
    [property: JsonPropertyName("owed")] double Owed);

/// <summary>
/// <c>Outstanding</c> is owed right now for counter bookings already played — it is not tied
/// to the period. <c>PlatformFee</c> and <c>Net</c> are for admins only, null otherwise.
/// </summary>
public sealed record MoneyReport(
    [property: JsonPropertyName("period")] PeriodInfo Period,
    [property: JsonPropertyName("collected")] Kpi Collected,
    [property: JsonPropertyName("booked")] Kpi Booked,
    [property: JsonPropertyName("outstanding")] double Outstanding,
    [property: JsonPropertyName("outstandingCount")] int OutstandingCount,
    [property: JsonPropertyName("platformFee")] Kpi? PlatformFee,
    [property: JsonPropertyName("net")] Kpi? Net,
    [property: JsonPropertyName("byMethod")] List<KeyAmount> ByMethod,
    [property: JsonPropertyName("byKind")] List<KeyAmount> ByKind,
    [property: JsonPropertyName("daily")] List<MoneyDay> Daily,
    [property: JsonPropertyName("byVenue")] List<VenueMoney> ByVenue,
    [property: JsonPropertyName("byPitch")] List<PitchMoney> ByPitch,
    [property: JsonPropertyName("outstandingItems")] List<OutstandingItem> OutstandingItems);

// ── Bookings health ───────────────────────────────────────────────────────────

public sealed record BookingsDay(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("app")] int App,
    [property: JsonPropertyName("counter")] int Counter,
    [property: JsonPropertyName("weekly")] int Weekly,
    [property: JsonPropertyName("series")] int Series,
    [property: JsonPropertyName("cancelled")] int Cancelled,
    [property: JsonPropertyName("web")] int Web);

public sealed record BookingsReport(
    [property: JsonPropertyName("period")] PeriodInfo Period,
    [property: JsonPropertyName("bookings")] Kpi Bookings,
    [property: JsonPropertyName("cancelRate")] Kpi CancelRate,
    [property: JsonPropertyName("noShowRate")] Kpi NoShowRate,
    [property: JsonPropertyName("attended")] int Attended,
    [property: JsonPropertyName("noShows")] int NoShows,
    [property: JsonPropertyName("cancelledByPerson")] int CancelledByPerson,
    [property: JsonPropertyName("cancelledExpired")] int CancelledExpired,
    [property: JsonPropertyName("byStatus")] List<KeyCount> ByStatus,
    [property: JsonPropertyName("byChannel")] List<KeyCount> ByChannel,
    [property: JsonPropertyName("daily")] List<BookingsDay> Daily,
    [property: JsonPropertyName("leadTime")] List<KeyCount> LeadTime,
    [property: JsonPropertyName("sports")] List<KeyCount> Sports);

// ── Busy hours ────────────────────────────────────────────────────────────────

/// <summary>One weekday × hour cell. Day 0 = Sunday. Pct null when nothing was open.</summary>
public sealed record OccupancyCell(
    [property: JsonPropertyName("day")] int Day,
    [property: JsonPropertyName("hour")] int Hour,
    [property: JsonPropertyName("openHours")] double OpenHours,
    [property: JsonPropertyName("bookedHours")] double BookedHours,
    [property: JsonPropertyName("pct")] double? Pct);

public sealed record PitchOccupancy(
    [property: JsonPropertyName("venueId")] string VenueId,
    [property: JsonPropertyName("venueName")] string VenueName,
    [property: JsonPropertyName("pitchId")] string PitchId,
    [property: JsonPropertyName("pitchName")] string PitchName,
    [property: JsonPropertyName("pitchNameAr")] string? PitchNameAr,
    [property: JsonPropertyName("openHours")] double OpenHours,
    [property: JsonPropertyName("bookedHours")] double BookedHours,
    [property: JsonPropertyName("pct")] double? Pct);

public sealed record OccupancyReport(
    [property: JsonPropertyName("period")] PeriodInfo Period,
    [property: JsonPropertyName("occupancy")] Kpi Occupancy,
    [property: JsonPropertyName("openHours")] double OpenHours,
    [property: JsonPropertyName("bookedHours")] double BookedHours,
    [property: JsonPropertyName("grid")] List<OccupancyCell> Grid,
    [property: JsonPropertyName("byPitch")] List<PitchOccupancy> ByPitch,
    [property: JsonPropertyName("busiest")] List<OccupancyCell> Busiest,
    [property: JsonPropertyName("quietest")] List<OccupancyCell> Quietest);

// ── Customers & team ──────────────────────────────────────────────────────────

public sealed record TopCustomer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("phone")] string Phone,
    [property: JsonPropertyName("visits")] int Visits,
    [property: JsonPropertyName("paid")] double Paid);

public sealed record CustomersSection(
    [property: JsonPropertyName("active")] Kpi Active,
    [property: JsonPropertyName("new")] Kpi New,
    [property: JsonPropertyName("returning")] int Returning,
    [property: JsonPropertyName("returnRate")] double ReturnRate,
    [property: JsonPropertyName("lapsed")] int Lapsed,
    [property: JsonPropertyName("topByVisits")] List<TopCustomer> TopByVisits,
    [property: JsonPropertyName("topBySpend")] List<TopCustomer> TopBySpend);

public sealed record TeamMember(
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("payments")] int Payments,
    [property: JsonPropertyName("collected")] double Collected,
    [property: JsonPropertyName("cash")] double Cash,
    [property: JsonPropertyName("cliq")] double Cliq,
    [property: JsonPropertyName("counterBookings")] int CounterBookings);

public sealed record CustomersReport(
    [property: JsonPropertyName("period")] PeriodInfo Period,
    [property: JsonPropertyName("customers")] CustomersSection? Customers,
    [property: JsonPropertyName("team")] List<TeamMember>? Team);

// ── Platform (admin) ──────────────────────────────────────────────────────────

public sealed record PlatformDay(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("booked")] double Booked,
    [property: JsonPropertyName("fee")] double Fee,
    [property: JsonPropertyName("bookings")] int Bookings);

public sealed record CompanyRow(
    [property: JsonPropertyName("ownerId")] string OwnerId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("nameAr")] string? NameAr,
    [property: JsonPropertyName("ownerStatus")] string OwnerStatus,
    [property: JsonPropertyName("venues")] int Venues,
    [property: JsonPropertyName("bookings")] int Bookings,
    [property: JsonPropertyName("booked")] double Booked,
    [property: JsonPropertyName("fee")] double Fee,
    [property: JsonPropertyName("collected")] double Collected);

public sealed record PlatformReport(
    [property: JsonPropertyName("period")] PeriodInfo Period,
    [property: JsonPropertyName("booked")] Kpi Booked,
    [property: JsonPropertyName("fee")] Kpi Fee,
    [property: JsonPropertyName("collected")] Kpi Collected,
    [property: JsonPropertyName("bookings")] Kpi Bookings,
    [property: JsonPropertyName("appShare")] Kpi AppShare,
    [property: JsonPropertyName("newCompanies")] Kpi NewCompanies,
    [property: JsonPropertyName("newVenues")] Kpi NewVenues,
    [property: JsonPropertyName("newPlayers")] Kpi NewPlayers,
    [property: JsonPropertyName("activeCompanies")] Kpi ActiveCompanies,
    [property: JsonPropertyName("daily")] List<PlatformDay> Daily,
    [property: JsonPropertyName("companies")] List<CompanyRow> Companies);
