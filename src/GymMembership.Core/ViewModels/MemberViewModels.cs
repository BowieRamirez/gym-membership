using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public abstract partial class LoadableViewModel : ObservableObject
{
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? notice;

    /// <summary>Called by the page each time it appears.</summary>
    public virtual Task OnAppearingAsync() => Task.CompletedTask;

    protected async Task<bool> RunAsync<T>(Func<Task<AppResult<T>>> operation, Action<T> onOk)
    {
        IsBusy = true; ErrorMessage = null;
        try
        {
            var result = await operation();
            if (result.Ok) { onOk(result.Value!); return true; }
            ErrorMessage = ErrorText.For(result.Error, result.Message);
            return false;
        }
        finally { IsBusy = false; }
    }

    protected Task<bool> RunAsync(Func<Task<AppResult<bool>>> operation) => RunAsync(operation, _ => { });

    protected static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    { target.Clear(); foreach (var r in rows) target.Add(r); }
}

public partial class LoginViewModel(IAuthService auth, IAppNavigator nav) : LoadableViewModel
{
    [ObservableProperty] private string email = "";
    [ObservableProperty] private string password = "";

    [RelayCommand]
    private Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        { ErrorMessage = "Enter your email and password."; return Task.CompletedTask; }
        return GoAsync(Email.Trim(), Password);
    }

    [RelayCommand] private Task DemoSignInAsync(string demoEmail) => GoAsync(demoEmail, DemoGateway.DemoPassword);

    async Task GoAsync(string e, string p)
    {
        var role = AppRole.Member;
        if (await RunAsync(() => auth.SignInAsync(e, p), r => role = r)) nav.GoTo(role);
    }
}

public partial class PackagesViewModel(IMembershipService svc, IProofPicker picker) : LoadableViewModel
{
    public ObservableCollection<MembershipPackage> Packages { get; } = new();
    [ObservableProperty] private string? discountCode;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.ListPackagesAsync, rows => Fill(Packages, rows));

    [RelayCommand]
    private async Task AvailAsync(MembershipPackage package)
    {
        Notice = null;
        var paymentId = 0;
        if (!await RunAsync(() => svc.AvailAsync(package.Id, DiscountCode), id => paymentId = id)) return;
        Notice = string.IsNullOrWhiteSpace(DiscountCode)
            ? "Payment created. Pay at the front desk, or attach your proof of payment now."
            : "Payment created with your discount. Pay at the front desk, or attach your proof of payment now.";
        if (await picker.PickAsync() is { } file && await RunAsync(() => svc.AttachProofAsync(paymentId, file.FileName, file.Data)))
            Notice = "Proof attached. Staff will verify it shortly.";
    }
}

public partial class MyMembershipViewModel(IMembershipService svc, IProofPicker picker) : LoadableViewModel
{
    public ObservableCollection<MembershipRow> Memberships { get; } = new();
    public ObservableCollection<PaymentItem> Payments { get; } = new();
    [ObservableProperty] private string heroNumber = "–";
    [ObservableProperty] private string heroCaption = "Pick a package to get started.";
    [ObservableProperty] private double laneFraction;
    [ObservableProperty] private string? discountCode;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(svc.MyMembershipsAsync, rows => { Fill(Memberships, rows); UpdateHero(rows); });
        await RunAsync(svc.MyPaymentsAsync, rows => Fill(Payments, rows));
    }

    void UpdateHero(IReadOnlyList<MembershipRow> rows)
    {
        var active = rows.FirstOrDefault(r => r.Status == Status.Active);
        var expired = rows.FirstOrDefault(r => r.Status == Status.Expired);
        if (active is { DaysLeft: { } left })
        {
            HeroNumber = left.ToString();
            HeroCaption = left == 1 ? $"day left on {active.PackageName}" : $"days left on {active.PackageName}";
            LaneFraction = Math.Clamp((double)left / Math.Max(1, active.DurationDays), 0, 1);
        }
        else if (expired is not null)
        { HeroNumber = "0"; HeroCaption = $"{expired.PackageName} has expired. Renew to get back in."; LaneFraction = 0; }
        else if (rows.Any(r => r.Status == Status.Pending))
        { HeroNumber = "–"; HeroCaption = "Waiting for staff to verify your payment."; LaneFraction = 0; }
        else
        { HeroNumber = "–"; HeroCaption = "Pick a package to get started."; LaneFraction = 0; }
    }

    [RelayCommand]
    private async Task RenewAsync(MembershipRow row)
    {
        Notice = null;
        var paymentId = 0;
        if (!await RunAsync(() => svc.RenewAsync(row.Id, DiscountCode), id => paymentId = id)) return;
        Notice = "Renewal payment created. Attach your proof of payment or pay at the front desk.";
        if (await picker.PickAsync() is { } file && await RunAsync(() => svc.AttachProofAsync(paymentId, file.FileName, file.Data)))
            Notice = "Proof attached. Staff will verify it shortly.";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AttachProofAsync(PaymentItem item)
    {
        if (await picker.PickAsync() is { } file && await RunAsync(() => svc.AttachProofAsync(item.Id, file.FileName, file.Data)))
        { Notice = "Proof attached. Staff will verify it shortly."; await LoadAsync(); }
    }
}

public partial class AmenitiesViewModel(IAmenityService svc, IProofPicker picker) : LoadableViewModel
{
    public ObservableCollection<AmenityRow> Items { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.CatalogAsync, rows => Fill(Items, rows));

    [RelayCommand]
    private async Task AvailAsync(AmenityRow row)
    {
        Notice = null;
        if (await RunAsync(() => svc.AvailAsync(row.Amenity.Id), _ => { })) await LoadAsync();
    }

    [RelayCommand]
    private async Task LogUsageAsync(AmenityRow row)
    {
        if (row.UserAmenityId is not { } id) return;
        var proof = await picker.PickAsync();
        if (await RunAsync(() => svc.LogUsageAsync(id, proof))) Notice = "Usage sent. Staff will verify it.";
    }
}

public partial class CoachesViewModel(ICoachService svc, IMessageService messages, IAppNavigator nav) : LoadableViewModel
{
    public ObservableCollection<CoachCard> Cards { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.ListCoachesAsync, rows => Fill(Cards, rows));

    [RelayCommand]
    private async Task HireAsync(CoachCard card)
    { if (await RunAsync(() => svc.HireAsync(card.Coach.Id))) await LoadAsync(); }

    [RelayCommand]
    private async Task EndHireAsync(CoachCard card)
    { if (card.HireId is { } id && await RunAsync(() => svc.EndHireAsync(id))) await LoadAsync(); }

    [RelayCommand]
    private void Message(CoachCard card) { messages.Pending = (card.Coach.Id, null); nav.GoToMessages(); }
}

public partial class MessagesViewModel(IMessageService svc) : LoadableViewModel
{
    public ObservableCollection<ThreadRow> Threads { get; } = new();
    public ObservableCollection<MessageRow> Messages { get; } = new();
    [ObservableProperty] private ThreadRow? selected;
    [ObservableProperty] private string draft = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReadOnly))] private bool canSend;

    /// <summary>A conversation is open but the hire has ended: history only.</summary>
    public bool ReadOnly => Selected is not null && !CanSend;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        var keep = Selected;
        if (!await RunAsync(svc.ThreadsAsync, rows => Fill(Threads, rows))) return;
        var want = svc.Pending; svc.Pending = null;
        Selected = Threads.FirstOrDefault(t => want is { } w && t.CoachId == w.CoachId && (w.MemberId is null || t.MemberId == w.MemberId))
            ?? Threads.FirstOrDefault(t => keep is not null && t.CoachId == keep.CoachId && t.MemberId == keep.MemberId)
            ?? Threads.FirstOrDefault();
    }

    partial void OnSelectedChanged(ThreadRow? value)
    {
        CanSend = value?.CanSend ?? false;
        OnPropertyChanged(nameof(ReadOnly));
        _ = ShowAsync();
    }

    async Task ShowAsync()
    {
        if (Selected is { } t) await RunAsync(() => svc.MessagesAsync(t.CoachId, t.MemberId), rows => Fill(Messages, rows));
        else Messages.Clear();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Selected is not { } t) return;
        if (await RunAsync(() => svc.SendAsync(t.CoachId, t.MemberId, Draft))) { Draft = ""; await ShowAsync(); }
    }
}

public partial class NotificationsViewModel(INotificationService svc) : LoadableViewModel
{
    public ObservableCollection<UserNotification> Items { get; } = new();
    [ObservableProperty] private int unreadCount;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.ListAsync, rows => { Fill(Items, rows); UnreadCount = rows.Count(n => !n.IsRead); });

    [RelayCommand]
    private async Task MarkReadAsync(UserNotification n)
    { if (!n.IsRead && await RunAsync(() => svc.MarkReadAsync(n.Id))) await LoadAsync(); }
}

public partial class SettingsViewModel(ISettingsService settings, IAuthService auth, IAppNavigator nav) : LoadableViewModel
{
    public string[] Themes { get; } = ["system", "light", "dark"];
    [ObservableProperty] private string theme = "system";

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(settings.LoadAsync, s => Theme = s.Theme);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (await RunAsync(() => settings.SaveThemeAsync(Theme))) { nav.ApplyTheme(Theme); Notice = "Theme saved."; }
    }

    [RelayCommand] private async Task SignOutAsync() { await auth.SignOutAsync(); nav.GoToLogin(); }
}
