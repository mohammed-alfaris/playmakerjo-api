namespace SportsVenueApi.Constants;

/// <summary>
/// Everything an owner can grant a staff member. Owners combine these into their own roles.
///
/// Deliberately absent, because they stay owner-only whatever a role says: editing or deleting
/// venues, managing staff and roles, and company settings. A permission that lets a clerk hire
/// more clerks or rewrite prices is not one to hand out from a checkbox.
/// </summary>
public static class StaffPermissions
{
    public const string BookingsView = "bookings.view";
    public const string BookingsManage = "bookings.manage";
    public const string PaymentsView = "payments.view";
    public const string PaymentsRecord = "payments.record";
    public const string CustomersView = "customers.view";
    public const string CustomersExport = "customers.export";
    public const string CustomersManage = "customers.manage";
    public const string StandingView = "standing.view";
    public const string StandingManage = "standing.manage";
    public const string ReportsView = "reports.view";

    public static readonly IReadOnlyList<string> All =
    [
        BookingsView, BookingsManage,
        PaymentsView, PaymentsRecord,
        CustomersView, CustomersExport, CustomersManage,
        StandingView, StandingManage,
        ReportsView,
    ];

    private static readonly HashSet<string> Known = new(All, StringComparer.Ordinal);

    public static bool IsValid(string key) => Known.Contains(key);

    /// <summary>
    /// Exactly what a "write" clerk could do before roles existed. Seeded as each company's
    /// "Front desk" role, and applied to any staff row that has no role yet.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyWrite =
    [
        BookingsView, BookingsManage,
        PaymentsView, PaymentsRecord,
        CustomersView, CustomersExport, CustomersManage,
        StandingView, StandingManage,
    ];

    /// <summary>
    /// Exactly what a "read" clerk could do before roles existed — including seeing the payment
    /// ledger and exporting customers, which read staff could already do. Preserved rather than
    /// quietly tightened, so deploying roles changes nobody's access; owners can untick them.
    /// </summary>
    public static readonly IReadOnlyList<string> LegacyRead =
    [
        BookingsView, PaymentsView, CustomersView, CustomersExport, StandingView,
    ];

    public const string LegacyWriteRoleName = "Front desk";
    public const string LegacyReadRoleName = "View only";
}
