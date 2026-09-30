using ClosedXML.Excel;
using SportsVenueApi.DTOs.Reports;

namespace SportsVenueApi.Services.Reports;

/// <summary>
/// The reports as an Excel workbook, for the owner's accountant. Built from the same report
/// objects the screen shows, so the file and the page cannot disagree about a number.
///
/// One sheet per area, only the areas the caller may see: no Customers sheet for a clerk
/// without customers.view, no Team sheet for any clerk, a Platform sheet for admins looking
/// at the whole platform. Arabic gets Arabic headings and a right-to-left layout.
/// </summary>
public static class ReportWorkbook
{
    public sealed record Input(
        string Title,
        ReportPeriod Period,
        MoneyReport Money,
        BookingsReport Bookings,
        OccupancyReport Occupancy,
        CustomersReport Customers,
        PlatformReport? Platform);

    private const string MoneyFormat = "#,##0.000";
    private const string PctFormat = "0.0\"%\"";

    public static byte[] Build(Input input, string lang)
    {
        var ar = lang == "ar";
        string L(string key) => (ar ? Ar : En).GetValueOrDefault(key, key);

        using var wb = new XLWorkbook();

        // ── Summary ──────────────────────────────────────────────────────────
        var summary = Sheet(wb, L("summary"), ar);
        summary.Cell(1, 1).Value = input.Title;
        summary.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        summary.Cell(2, 1).Value = $"{L("period")}: {input.Period.From:yyyy-MM-dd} → {input.Period.To:yyyy-MM-dd}";
        summary.Cell(3, 1).Value = $"{L("generated")}: {DateTime.UtcNow.AddHours(3):yyyy-MM-dd HH:mm} (Amman)";

        var rows = new List<(string Label, double Value, string Format)>
        {
            (L("collected"), input.Money.Collected.Value, MoneyFormat),
            (L("booked"), input.Money.Booked.Value, MoneyFormat),
            (L("outstanding"), input.Money.Outstanding, MoneyFormat),
        };
        if (input.Money.PlatformFee != null) rows.Add((L("platform_fee"), input.Money.PlatformFee.Value, MoneyFormat));
        if (input.Money.Net != null) rows.Add((L("net"), input.Money.Net.Value, MoneyFormat));
        rows.Add((L("bookings"), input.Bookings.Bookings.Value, "0"));
        rows.Add((L("cancel_rate"), input.Bookings.CancelRate.Value, PctFormat));
        rows.Add((L("no_show_rate"), input.Bookings.NoShowRate.Value, PctFormat));
        rows.Add((L("occupancy"), input.Occupancy.Occupancy.Value, PctFormat));
        if (input.Customers.Customers != null)
        {
            rows.Add((L("active_customers"), input.Customers.Customers.Active.Value, "0"));
            rows.Add((L("new_customers"), input.Customers.Customers.New.Value, "0"));
            rows.Add((L("return_rate"), input.Customers.Customers.ReturnRate, PctFormat));
        }
        var r = 5;
        foreach (var (label, value, format) in rows)
        {
            summary.Cell(r, 1).Value = label;
            summary.Cell(r, 2).Value = value;
            summary.Cell(r, 2).Style.NumberFormat.Format = format;
            r++;
        }
        summary.Column(1).Width = 30;
        summary.Column(2).Width = 18;

        // ── Money ────────────────────────────────────────────────────────────
        Table(Sheet(wb, L("money"), ar),
            [L("date"), L("cash"), L("cliq"), L("other"), L("collected"), L("booked")],
            input.Money.Daily.Select(d => new object?[] { d.Date, d.Cash, d.Cliq, d.Other, d.Cash + d.Cliq + d.Other, d.Booked }),
            [null, MoneyFormat, MoneyFormat, MoneyFormat, MoneyFormat, MoneyFormat], totals: true);

        var venues = Sheet(wb, L("venues"), ar);
        var next = Table(venues,
            [L("venue"), L("collected"), L("booked"), L("bookings")],
            input.Money.ByVenue.Select(v => new object?[] { ar && v.NameAr != null ? v.NameAr : v.Name, v.Collected, v.Booked, v.Bookings }),
            [null, MoneyFormat, MoneyFormat, "0"], totals: true);
        Table(venues,
            [L("venue"), L("pitch"), L("booked"), L("bookings")],
            input.Money.ByPitch.Select(p => new object?[] { p.VenueName, ar && p.PitchNameAr != null ? p.PitchNameAr : p.PitchName, p.Booked, p.Bookings }),
            [null, null, MoneyFormat, "0"], startRow: next + 2);

        Table(Sheet(wb, L("outstanding"), ar),
            [L("date"), L("time"), L("venue"), L("customer"), L("phone"), L("total"), L("paid"), L("owed")],
            input.Money.OutstandingItems.Select(o => new object?[]
                { o.Date, o.StartTime, o.VenueName, o.CustomerName, o.CustomerPhone, o.Total, o.Paid, o.Owed }),
            [null, null, null, null, "@", MoneyFormat, MoneyFormat, MoneyFormat], totals: true);

        // ── Busy hours: weekday rows × hour columns, only the hours anything was open ──
        var busy = Sheet(wb, L("busy_hours"), ar);
        var hours = input.Occupancy.Grid.Where(c => c.Pct != null).Select(c => c.Hour).Distinct().OrderBy(h => h).ToList();
        busy.Cell(1, 1).Value = L("day");
        for (var i = 0; i < hours.Count; i++) busy.Cell(1, i + 2).Value = $"{hours[i]:00}:00";
        for (var d = 0; d < 7; d++)
        {
            busy.Cell(d + 2, 1).Value = L($"dow_{d}");
            for (var i = 0; i < hours.Count; i++)
            {
                var cell = input.Occupancy.Grid.Single(c => c.Day == d && c.Hour == hours[i]);
                if (cell.Pct == null) continue;
                busy.Cell(d + 2, i + 2).Value = cell.Pct.Value;
                busy.Cell(d + 2, i + 2).Style.NumberFormat.Format = PctFormat;
            }
        }
        StyleHeader(busy.Row(1));
        if (hours.Count > 0)
            busy.Range(2, 2, 8, hours.Count + 1).AddConditionalFormat().ColorScale()
                .LowestValue(XLColor.White).HighestValue(XLColor.FromHtml("#2E7D32"));
        Table(busy,
            [L("venue"), L("pitch"), L("open_hours"), L("booked_hours"), L("occupancy")],
            input.Occupancy.ByPitch.Select(p => new object?[] { p.VenueName, ar && p.PitchNameAr != null ? p.PitchNameAr : p.PitchName, p.OpenHours, p.BookedHours, p.Pct }),
            [null, null, "0.0", "0.0", PctFormat], startRow: 11);

        // ── Bookings ─────────────────────────────────────────────────────────
        Table(Sheet(wb, L("bookings"), ar),
            [L("date"), L("ch_app"), L("ch_counter"), L("ch_weekly"), L("ch_series"), L("cancelled")],
            input.Bookings.Daily.Select(d => new object?[] { d.Date, d.App, d.Counter, d.Weekly, d.Series, d.Cancelled }),
            [null, "0", "0", "0", "0", "0"], totals: true);

        // ── Customers & team ─────────────────────────────────────────────────
        if (input.Customers.Customers is { } c)
        {
            var cs = Sheet(wb, L("customers"), ar);
            var after = Table(cs, [L("top_by_visits"), L("phone"), L("visits"), L("paid")],
                c.TopByVisits.Select(t => new object?[] { t.Name, t.Phone, t.Visits, t.Paid }), [null, "@", "0", MoneyFormat]);
            Table(cs, [L("top_by_spend"), L("phone"), L("visits"), L("paid")],
                c.TopBySpend.Select(t => new object?[] { t.Name, t.Phone, t.Visits, t.Paid }), [null, "@", "0", MoneyFormat],
                startRow: after + 2);
        }
        if (input.Customers.Team is { } team)
        {
            Table(Sheet(wb, L("team"), ar),
                [L("person"), L("payments"), L("collected"), L("cash"), L("cliq"), L("counter_bookings")],
                team.Select(m => new object?[] { m.UserId == null ? L("via_app") : m.Name, m.Payments, m.Collected, m.Cash, m.Cliq, m.CounterBookings }),
                [null, "0", MoneyFormat, MoneyFormat, MoneyFormat, "0"], totals: true);
        }

        // ── Platform ─────────────────────────────────────────────────────────
        if (input.Platform is { } platform)
        {
            Table(Sheet(wb, L("platform"), ar),
                [L("company"), L("status"), L("venues"), L("bookings"), L("booked"), L("platform_fee"), L("collected")],
                platform.Companies.Select(co => new object?[]
                    { ar && co.NameAr != null ? co.NameAr : co.Name, co.OwnerStatus, co.Venues, co.Bookings, co.Booked, co.Fee, co.Collected }),
                [null, null, "0", "0", MoneyFormat, MoneyFormat, MoneyFormat], totals: true);
        }

        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }

    private static IXLWorksheet Sheet(XLWorkbook wb, string name, bool rtl)
    {
        var ws = wb.Worksheets.Add(name.Length > 31 ? name[..31] : name);
        ws.RightToLeft = rtl;
        return ws;
    }

    private static void StyleHeader(IXLRow row)
    {
        row.Style.Font.SetBold();
        row.Style.Fill.SetBackgroundColor(XLColor.FromHtml("#EEF2EE"));
    }

    /// <summary>
    /// Writes a header row and data rows at <paramref name="startRow"/>, optionally a totals row
    /// summing every numeric column. Returns the last row written.
    /// </summary>
    private static int Table(IXLWorksheet ws, string[] headers, IEnumerable<object?[]> data, string?[] formats,
        int startRow = 1, bool totals = false)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(startRow, i + 1).Value = headers[i];
        StyleHeader(ws.Row(startRow));

        var r = startRow;
        foreach (var values in data)
        {
            r++;
            for (var i = 0; i < values.Length; i++)
            {
                var cell = ws.Cell(r, i + 1);
                cell.Value = values[i] switch
                {
                    null => Blank.Value,
                    double d => d,
                    int n => n,
                    string s when formats[i] == "@" => s, // phone numbers stay text
                    var other => other.ToString(),
                };
                if (formats[i] is { } f && f != "@") cell.Style.NumberFormat.Format = f;
                if (formats[i] == "@") cell.Style.NumberFormat.Format = "@";
            }
        }

        if (totals && r > startRow)
        {
            r++;
            ws.Cell(r, 1).Value = "Σ";
            for (var i = 1; i < headers.Length; i++)
            {
                if (formats[i] is null or "@" || formats[i] == PctFormat) continue;
                var col = ws.Cell(startRow, i + 1).Address.ColumnLetter;
                ws.Cell(r, i + 1).FormulaA1 = $"SUM({col}{startRow + 1}:{col}{r - 1})";
                ws.Cell(r, i + 1).Style.NumberFormat.Format = formats[i];
            }
            ws.Row(r).Style.Font.SetBold();
        }

        if (startRow == 1) ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Length).AdjustToContents(startRow, r);
        return r;
    }

    private static readonly Dictionary<string, string> En = new()
    {
        ["summary"] = "Summary", ["period"] = "Period", ["generated"] = "Generated",
        ["collected"] = "Collected", ["booked"] = "Booked value", ["outstanding"] = "Outstanding",
        ["platform_fee"] = "Platform fee", ["net"] = "Owner net", ["bookings"] = "Bookings",
        ["cancel_rate"] = "Cancellation rate", ["no_show_rate"] = "No-show rate", ["occupancy"] = "Occupancy",
        ["active_customers"] = "Active customers", ["new_customers"] = "New customers", ["return_rate"] = "Return rate",
        ["money"] = "Money", ["date"] = "Date", ["cash"] = "Cash", ["cliq"] = "CliQ", ["other"] = "Other",
        ["venues"] = "Venues & pitches", ["venue"] = "Venue", ["pitch"] = "Pitch",
        ["time"] = "Time", ["customer"] = "Customer", ["phone"] = "Phone", ["total"] = "Total", ["paid"] = "Paid", ["owed"] = "Owed",
        ["busy_hours"] = "Busy hours", ["day"] = "Day", ["open_hours"] = "Open hours", ["booked_hours"] = "Booked hours",
        ["dow_0"] = "Sunday", ["dow_1"] = "Monday", ["dow_2"] = "Tuesday", ["dow_3"] = "Wednesday",
        ["dow_4"] = "Thursday", ["dow_5"] = "Friday", ["dow_6"] = "Saturday",
        ["ch_app"] = "App", ["ch_counter"] = "Counter", ["ch_weekly"] = "Weekly", ["ch_series"] = "Series", ["cancelled"] = "Cancelled",
        ["customers"] = "Customers", ["top_by_visits"] = "Top by visits", ["top_by_spend"] = "Top by spend", ["visits"] = "Visits",
        ["team"] = "Team", ["person"] = "Person", ["payments"] = "Payments", ["counter_bookings"] = "Counter bookings",
        ["via_app"] = "Through the app", ["platform"] = "Platform", ["company"] = "Company", ["status"] = "Status",
    };

    private static readonly Dictionary<string, string> Ar = new()
    {
        ["summary"] = "الملخص", ["period"] = "الفترة", ["generated"] = "تاريخ الإنشاء",
        ["collected"] = "المقبوض", ["booked"] = "قيمة الحجوزات", ["outstanding"] = "المستحق",
        ["platform_fee"] = "عمولة المنصة", ["net"] = "صافي المالك", ["bookings"] = "الحجوزات",
        ["cancel_rate"] = "نسبة الإلغاء", ["no_show_rate"] = "نسبة عدم الحضور", ["occupancy"] = "نسبة الإشغال",
        ["active_customers"] = "الزبائن النشطون", ["new_customers"] = "زبائن جدد", ["return_rate"] = "نسبة العودة",
        ["money"] = "المال", ["date"] = "التاريخ", ["cash"] = "كاش", ["cliq"] = "كليك", ["other"] = "أخرى",
        ["venues"] = "الملاعب", ["venue"] = "المجمع", ["pitch"] = "الملعب",
        ["time"] = "الوقت", ["customer"] = "الزبون", ["phone"] = "الهاتف", ["total"] = "المجموع", ["paid"] = "المدفوع", ["owed"] = "المتبقي",
        ["busy_hours"] = "أوقات الذروة", ["day"] = "اليوم", ["open_hours"] = "ساعات العمل", ["booked_hours"] = "الساعات المحجوزة",
        ["dow_0"] = "الأحد", ["dow_1"] = "الاثنين", ["dow_2"] = "الثلاثاء", ["dow_3"] = "الأربعاء",
        ["dow_4"] = "الخميس", ["dow_5"] = "الجمعة", ["dow_6"] = "السبت",
        ["ch_app"] = "التطبيق", ["ch_counter"] = "الكاونتر", ["ch_weekly"] = "أسبوعي", ["ch_series"] = "سلسلة", ["cancelled"] = "ملغي",
        ["customers"] = "الزبائن", ["top_by_visits"] = "الأكثر زيارة", ["top_by_spend"] = "الأكثر دفعاً", ["visits"] = "الزيارات",
        ["team"] = "الفريق", ["person"] = "الشخص", ["payments"] = "الدفعات", ["counter_bookings"] = "حجوزات الكاونتر",
        ["via_app"] = "عبر التطبيق", ["platform"] = "المنصة", ["company"] = "الشركة", ["status"] = "الحالة",
    };
}
