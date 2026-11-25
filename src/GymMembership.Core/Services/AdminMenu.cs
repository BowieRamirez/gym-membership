namespace GymMembership.Core.Services;

/// <summary>One menu row: the words shown, the shell page it opens (by title; null = a fold that only opens its children), and the
/// tab it opens that page on. Entries for pages a role doesn't have are left out when the menu is built.</summary>
public sealed record AdminMenuEntry(string Label, string Icon, string? Page, string? Tab = null, IReadOnlyList<AdminMenuEntry>? Children = null)
{
    public static AdminMenuEntry To(string page, string icon, string? tab = null) => new(page, icon, page, tab);
}

public sealed record AdminMenuSection(string Title, IReadOnlyList<AdminMenuEntry> Entries);

/// <summary>Owner fixes 2026-11-25, Fix 4 (W1, W3): the System admin's and Admin's menu with words. Messages and Settings stay at the bottom.</summary>
public static class AdminMenu
{
    public static readonly IReadOnlyList<AdminMenuSection> Sections =
    [
        new("Overview", [
            AdminMenuEntry.To("Overview", "layout-dashboard"),
            new("Front desk", "briefcase", null, Children: [
                AdminMenuEntry.To("Home", "house"), AdminMenuEntry.To("Cashier", "banknote"),
                AdminMenuEntry.To("Check-ins", "scan-line"), AdminMenuEntry.To("Amenity status", "waves")]),
        ]),
        new("Workforce Management", [
            AdminMenuEntry.To("Employees", "user-round"),
            AdminMenuEntry.To("Scheduling", "calendar-clock"),
            new("Attendance", "clipboard-check", null, Children: [
                new("Attendance Review", "clipboard-check", "Attendance", "review"),
                new("Attendance Records", "clock", "Attendance", "records"),
                new("Attendance Corrections", "sliders-horizontal", "Attendance", "corrections")]),
        ]),
        new("Finance & Payroll", [
            new("Payroll", "wallet", null, Children: [
                new("Salary Configuration", "settings", "Payroll", "salaries"),
                new("Payroll Processing", "receipt", "Payroll", "period"),
                new("Payslips", "id-card", "Payroll", "payslips"),
                new("Payroll History", "archive", "Payroll", "paid_out")]),
            AdminMenuEntry.To("Revenue", "chart-line"), AdminMenuEntry.To("Expenses", "receipt"), AdminMenuEntry.To("Financial reports", "landmark"), AdminMenuEntry.To("Refunds", "undo"), AdminMenuEntry.To("Discounts", "tag"),
        ]),
        new("Gym", [
            AdminMenuEntry.To("Users and roles", "users"), AdminMenuEntry.To("Packages and amenities", "package"),
            AdminMenuEntry.To("Announcements", "megaphone"), AdminMenuEntry.To("Damage reports", "alert-circle"),
            AdminMenuEntry.To("Gym settings", "sliders-horizontal"), AdminMenuEntry.To("Logs & Archive", "archive"),
        ]),
    ];

    /// <summary>Every row that opens a page, children included.</summary>
    public static IEnumerable<AdminMenuEntry> Links => Sections.SelectMany(s => s.Entries).SelectMany(e => e.Children ?? [e]).Where(e => e.Page is not null);
}

/// <summary>Sent by Attendance when its Pending count changes, so the menu's tag follows without asking the server again.</summary>
public sealed record AttendancePendingChanged(int Count);

/// <summary>A page the menu can open on one of its tabs (Fix 4): Payroll and Attendance.</summary>
public interface IOpensTab
{
    void OpenTab(string tab);
}
