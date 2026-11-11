using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

/// <summary>One admin page on the overview. Value is the big number in the strip, or the line under a link's name.</summary>
public partial class AdminOverviewCard(string key, string title, string? label = null) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    /// <summary>What the number in the strip is called; null for the pages listed as links.</summary>
    public string? Label { get; } = label;
    [ObservableProperty] string value = "Loading…";
    [ObservableProperty] string detail = "";
    [ObservableProperty] bool isUnavailable;
}

public partial class AdminOverviewViewModel(IAdminService admin, ISubscriptionService subscriptions, IAttendanceService? checkIns = null, IAnnouncementService? announcements = null) : LoadableViewModel
{
    /// <summary>D12: who checked in on the chosen Manila day, earliest first.</summary>
    public ObservableCollection<CheckInRow> CheckIns { get; } = new();
    [ObservableProperty] DateTime checkInDay = DateTime.UtcNow.AddHours(8).Date;
    [ObservableProperty] string checkInsText = "Loading check-ins…";
    partial void OnCheckInDayChanged(DateTime value) => _ = LoadCheckInsAsync();
    /// <summary>F8: the "Manual check-ins" list: only those made by name, with who recorded each and any reason.</summary>
    [ObservableProperty] bool byNameOnly;
    partial void OnByNameOnlyChanged(bool value) => _ = LoadCheckInsAsync();

    public async Task LoadCheckInsAsync()
    {
        if (checkIns is null) return;
        CheckInsText = "Loading check-ins…";
        var r = await checkIns.CheckInsOnAsync(DateOnly.FromDateTime(CheckInDay));
        CheckIns.Clear();
        if (!r.Ok) { CheckInsText = ErrorText.For(r.Error, r.Message); return; }
        var rows = r.Value!.Where(c => !ByNameOnly || c.Method == "manual").ToList();
        foreach (var row in rows) CheckIns.Add(row);
        var kind = ByNameOnly ? " by name" : "";
        CheckInsText = rows.Count switch { 0 => $"No check-ins{kind} on this day.", 1 => $"1 check-in{kind}", var n => $"{n} check-ins{kind}" };
    }

    public IReadOnlyList<AdminOverviewCard> Cards { get; } = [
        new("users", "Users and roles", "Accounts"), new("catalog", "Packages and amenities", "Packages"),
        new("discounts", "Discounts", "Discount codes"), new("refunds", "Refunds & voids"),
        new("revenue", "Revenue", "Revenue today"), new("announcements", "Announcements"),
        new("gym", "Gym settings"), new("audit", "Logs & Archive")];
    /// <summary>The number strip, in the order shown; every role on the overview has these four pages.</summary>
    public IEnumerable<AdminOverviewCard> Figures => new[] { "revenue", "users", "catalog", "discounts" }.Select(k => Cards.Single(c => c.Key == k));
    /// <summary>The pages without a number, listed as links. Empty for a sub-admin.</summary>
    public IEnumerable<AdminOverviewCard> Links => Shown.Where(c => c.Label is null);
    /// <summary>False for a sub-admin (D15): the cards for super-admin pages are neither shown nor loaded.</summary>
    public bool SuperAdmin { get; set; } = true;
    static readonly string[] SuperAdminOnly = ["refunds", "gym", "audit"];
    public IEnumerable<AdminOverviewCard> Shown => Cards.Where(c => SuperAdmin || !SuperAdminOnly.Contains(c.Key));
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    [ObservableProperty] string updatedText = "Loading admin summaries…";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasActivityMessage))] string recentActivity = "Loading activity…";
    public bool HasActivityMessage => RecentActivity.Length > 0;
    /// <summary>Newest audit events, newest first. RecentActivity holds the message when there are none.</summary>
    public ObservableCollection<ActivityRow> Activity { get; } = new();
    /// <summary>The lime bar: pending refunds, the one thing on the overview that needs a decision. Empty when none are waiting.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAttention))] string attentionText = "";
    public bool HasAttention => AttentionText.Length > 0;

    public override Task OnAppearingAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        UpdatedText = "Refreshing admin summaries…";
        RecentActivity = "Loading activity…";
        Activity.Clear();
        AttentionText = "";
        var now = Clock();
        var today = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
        var month = new DateOnly(today.Year, today.Month, 1);
        try
        {
            await Task.WhenAll(
                Load("users", async () =>
                {
                    var rows = Require(await admin.UsersAsync());
                    return ($"{rows.Count}", $"{rows.Count(r => r.IsActive)} active, {rows.Count(r => !r.IsActive)} inactive");
                }),
                Load("catalog", async () =>
                {
                    var packages = Require(await admin.AllPackagesAsync());
                    var amenities = Require(await admin.AllAmenitiesAsync());
                    var open = amenities.Count(a => a.IsActive);
                    return ($"{packages.Count(p => p.IsActive)}", $"active, plus {open} amenit{(open == 1 ? "y" : "ies")}");
                }),
                Load("discounts", async () =>
                {
                    var rows = Require(await admin.DiscountsAsync());
                    var usable = rows.Count(r => r.Discount.IsActive && r.Discount.StartsAt <= now.UtcDateTime && r.Discount.ExpiresAt > now.UtcDateTime);
                    return ($"{usable}", $"usable now, of {rows.Count}");
                }),
                Load("refunds", async () =>
                {
                    var rows = Require(await subscriptions.RefundsAsync());
                    var pending = rows.Where(r => r.Request.Status == "pending").ToList();
                    var used = pending.Count(r => r.Request.WasActivated == true);
                    AttentionText = pending.Count == 0 ? "" :
                        (pending.Count == 1 ? "1 refund request is waiting for review" : $"{pending.Count} refund requests are waiting for review") +
                        (used == 0 ? "." : pending.Count == 1 ? ", for a term the member has used." : $", {used} of them for a term the member has used.");
                    var repay = rows.Count(r => r.Request.Status == "approved");
                    return ((pending.Count == 0 ? "Nothing to review" : $"{pending.Count} to review") + $", {repay} waiting for repayment", "");
                }),
                Load("revenue", async () =>
                {
                    var daily = Require(await admin.RevenueAsync(today, today));
                    var monthly = Require(await admin.RevenueAsync(month, today));
                    return (Money(daily.Sum(r => r.Total)), $"{Money(monthly.Sum(r => r.Total))} this month");
                }),
                Load("announcements", async () =>
                {
                    if (announcements is null) return ("Write to members and coaches", "");
                    var last = Require(await announcements.ListAsync()).FirstOrDefault();   // newest first
                    return (last is null ? "Nothing posted yet" : $"Last posted {last.WhenText}", "");
                }),
                Load("gym", async () =>
                {
                    var settings = Require(await admin.GymSettingsAsync());
                    return (string.IsNullOrWhiteSpace(settings.GymName) ? "Gym name not set" : settings.GymName, "");
                }),
                Load("audit", async () =>
                {
                    var latest = Require(await admin.AuditPageAsync(new(), 0)).Rows.Take(5).ToList();   // newest first from the server
                    foreach (var r in latest) Activity.Add(new(r.ActionText, r.ActorName, When(r.Manila, today)));
                    RecentActivity = latest.Count == 0 ? "No activity yet." : "";
                    return ("Full audit log and archived records", "");
                }));
            if (!SuperAdmin) RecentActivity = "";
            else if (Cards.Single(c => c.Key == "audit").IsUnavailable)
                RecentActivity = "Couldn't load recent activity. Refresh to try again.";
            var failed = Shown.Count(c => c.IsUnavailable);
            UpdatedText = $"{today:dddd, d MMM yyyy} · Checked {Clock().ToOffset(TimeSpan.FromHours(8)):h:mm tt} Manila" +
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
    static string Money(decimal amount) => "₱" + amount.ToString("N2", CultureInfo.InvariantCulture);
    static string When(DateTime manila, DateOnly today) => DateOnly.FromDateTime(manila) == today
        ? manila.ToString("h:mm tt", CultureInfo.InvariantCulture)
        : DateOnly.FromDateTime(manila) == today.AddDays(-1) ? "Yesterday" : manila.ToString("d MMM", CultureInfo.InvariantCulture);
}

/// <summary>One recent audit event on the overview, with its readable label (item 14).</summary>
public sealed record ActivityRow(string Action, string Actor, string WhenText);
