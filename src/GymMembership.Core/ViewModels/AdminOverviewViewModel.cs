using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class AdminOverviewCard(string key, string title) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    [ObservableProperty] string value = "Loading…";
    [ObservableProperty] string detail = "";
    [ObservableProperty] bool isUnavailable;
}

public partial class AdminOverviewViewModel(IAdminService admin, ISubscriptionService subscriptions, IAttendanceService? checkIns = null) : LoadableViewModel
{
    /// <summary>D12: who checked in on the chosen Manila day, earliest first.</summary>
    public ObservableCollection<CheckInRow> CheckIns { get; } = new();
    [ObservableProperty] DateTime checkInDay = DateTime.UtcNow.AddHours(8).Date;
    [ObservableProperty] string checkInsText = "Loading check-ins…";
    partial void OnCheckInDayChanged(DateTime value) => _ = LoadCheckInsAsync();

    public async Task LoadCheckInsAsync()
    {
        if (checkIns is null) return;
        CheckInsText = "Loading check-ins…";
        var r = await checkIns.CheckInsOnAsync(DateOnly.FromDateTime(CheckInDay));
        CheckIns.Clear();
        if (!r.Ok) { CheckInsText = ErrorText.For(r.Error, r.Message); return; }
        foreach (var row in r.Value!) CheckIns.Add(row);
        CheckInsText = r.Value.Count switch { 0 => "No check-ins on this day.", 1 => "1 check-in", var n => $"{n} check-ins" };
    }

    public IReadOnlyList<AdminOverviewCard> Cards { get; } = [
        new("users", "Users and roles"), new("catalog", "Packages and amenities"),
        new("discounts", "Discounts & Archive"), new("refunds", "Cancellations & refunds"),
        new("revenue", "Revenue"), new("void", "Void a payment"),
        new("gym", "Gym settings"), new("audit", "Audit log")];
    /// <summary>False for a sub-admin (D15): the cards for super-admin pages are neither shown nor loaded.</summary>
    public bool SuperAdmin { get; set; } = true;
    static readonly string[] SuperAdminOnly = ["refunds", "void", "gym", "audit"];
    public IEnumerable<AdminOverviewCard> Shown => Cards.Where(c => SuperAdmin || !SuperAdminOnly.Contains(c.Key));
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    [ObservableProperty] string updatedText = "Loading admin summaries…";
    [ObservableProperty] string recentActivity = "Loading activity…";

    public override Task OnAppearingAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        UpdatedText = "Refreshing admin summaries…";
        RecentActivity = "Loading activity…";
        var now = Clock();
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var month = new DateOnly(today.Year, today.Month, 1);
        var shortcut = Cards.Single(c => c.Key == "void");
        shortcut.Value = "Open page";
        shortcut.Detail = "Review a verified payment and void it with a reason. No payment is changed here.";
        try
        {
            await Task.WhenAll(
                Load("users", async () =>
                {
                    var rows = Require(await admin.UsersAsync());
                    return ($"{rows.Count} account{Plural(rows.Count)} listed",
                        $"{rows.Count(r => r.IsActive)} active · {rows.Count(r => !r.IsActive)} inactive\n" +
                        $"Roles: {rows.Count(r => r.Roles.Contains("member"))} member · {rows.Count(r => r.Roles.Contains("coach"))} coach · {rows.Count(r => r.Roles.Contains("employee"))} front desk · {rows.Count(r => r.Roles.Contains("subadmin"))} sub-admin · {rows.Count(r => r.Roles.Contains("admin"))} super admin. Accounts may have multiple roles.");
                }),
                Load("catalog", async () =>
                {
                    var packages = Require(await admin.AllPackagesAsync());
                    var amenities = Require(await admin.AllAmenitiesAsync());
                    var active = packages.Count(p => p.IsActive);
                    return ($"{active} active package{Plural(active)}", $"{amenities.Count(a => a.IsActive)} active amenities in the loaded catalog. Manage plans, prices and amenity photos.");
                }),
                Load("discounts", async () =>
                {
                    var rows = Require(await admin.DiscountsAsync());
                    var usable = rows.Count(r => r.Discount.IsActive && r.Discount.StartsAt <= now.UtcDateTime && r.Discount.ExpiresAt > now.UtcDateTime);
                    return ($"{usable} usable code{Plural(usable)}", $"From {rows.Count} listed codes. Future, expired and disabled codes are excluded from the usable count.");
                }),
                Load("refunds", async () =>
                {
                    var rows = Require(await subscriptions.RefundsAsync());
                    return ($"{rows.Count(r => r.Request.Status == "pending")} awaiting review", $"{rows.Count(r => r.Request.Status == "approved")} awaiting manual repayment in the loaded queue. Approval and repayment are separate steps.");
                }),
                Load("revenue", async () =>
                {
                    var daily = Require(await admin.RevenueAsync(today, today));
                    var monthly = Require(await admin.RevenueAsync(month, today));
                    return ($"{Money(daily.Sum(r => r.Total))} today", $"{Money(monthly.Sum(r => r.Total))} month to date\nManila dates. Net of refunds on approval; not the cash drawer balance.");
                }),
                Load("gym", async () =>
                {
                    var settings = Require(await admin.GymSettingsAsync());
                    return (string.IsNullOrWhiteSpace(settings.GymName) ? "Gym name not set" : settings.GymName,
                        "Manage gym details, payment QR and receipt settings.");
                }),
                Load("audit", async () =>
                {
                    var rows = Require(await admin.AuditAsync());
                    var latest = rows.OrderByDescending(r => r.Entry.CreatedAt).Take(5).ToList();
                    RecentActivity = latest.Count == 0 ? "No activity in the loaded audit list." : string.Join("\n\n", latest.Select(r =>
                        $"{r.Entry.Action} · {r.Actor}\n{Manila(r.Entry.CreatedAt):dd MMM yyyy, HH:mm} Manila"));
                    return (latest.Count == 0 ? "No activity listed" : latest[0].Entry.Action, "Five newest events from the loaded audit list appear below. Open the audit log for details.");
                }));
            if (!SuperAdmin) RecentActivity = "Recent activity is in the super admin's Audit log.";
            else if (Cards.Single(c => c.Key == "audit").IsUnavailable)
                RecentActivity = "Activity unavailable. Retry the Audit log summary or open the full page.";
            var failed = Shown.Count(c => c.IsUnavailable);
            UpdatedText = $"{today:dddd, dd MMM yyyy} · Checked {Clock().ToOffset(TimeSpan.FromHours(8)):HH:mm} Manila" +
                (failed > 0 ? $" · {failed} summar{(failed == 1 ? "y" : "ies")} unavailable" : "");
        }
        finally { IsBusy = false; }
        await LoadCheckInsAsync();
    }

    async Task Load(string key, Func<Task<(string Value, string Detail)>> read)
    {
        var card = Cards.Single(c => c.Key == key);
        if (!Shown.Contains(card)) return;
        card.Value = "Loading…";
        card.Detail = "";
        card.IsUnavailable = false;
        try
        {
            var summary = await read();
            card.Value = summary.Value;
            card.Detail = summary.Detail;
        }
        catch (Exception ex)
        {
            card.Value = "Unavailable";
            card.Detail = ex is SummaryUnavailableException ? ex.Message : ErrorText.For(ErrorMapper.Map(ex));
            card.IsUnavailable = true;
        }
    }

    static T Require<T>(AppResult<T> result) => result.Ok ? result.Value! : throw new SummaryUnavailableException(ErrorText.For(result.Error, result.Message));
    sealed class SummaryUnavailableException(string message) : Exception(message);
    static string Money(decimal amount) => "₱ " + amount.ToString("N2", CultureInfo.InvariantCulture);
    static string Plural(int count) => count == 1 ? "" : "s";
    static DateTime Manila(DateTime value) => (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).AddHours(8);
}
