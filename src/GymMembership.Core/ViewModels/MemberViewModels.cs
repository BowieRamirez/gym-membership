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

public partial class LoginViewModel(IAuthService auth, IAppNavigator nav, ISupabaseGateway gateway) : LoadableViewModel
{
    /// <summary>True while running on the in-memory demo, which offers one-tap sign-in as each role.</summary>
    public bool IsDemo => gateway is DemoGateway;

    [ObservableProperty] private string email = "";
    [ObservableProperty] private string password = "";

    // someone who signed in last time and never signed out goes straight in
    public override async Task OnAppearingAsync()
    {
        AppRole? role = null;
        if (!await RunAsync(auth.RestoreAsync, r => role = r) || role is not { } found) { ErrorMessage = null; return; }
        var mustChange = false;
        await RunAsync(auth.MustChangePasswordAsync, m => mustChange = m);
        nav.GoTo(found, mustChange);
    }

    [RelayCommand]
    private Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrEmpty(Password))
        { ErrorMessage = "Enter your email and password."; return Task.CompletedTask; }
        return GoAsync(Email.Trim(), Password);
    }

    [RelayCommand] private void CreateAccount() => nav.GoToRegister();

    [RelayCommand] private Task DemoSignInAsync(string demoEmail) => GoAsync(demoEmail, DemoGateway.DemoPassword);

    async Task GoAsync(string e, string p)
    {
        var role = AppRole.Member;
        if (!await RunAsync(() => auth.SignInAsync(e, p), r => role = r)) return;
        var mustChange = false;
        await RunAsync(auth.MustChangePasswordAsync, m => mustChange = m);   // a temporary password goes straight to Settings
        nav.GoTo(role, mustChange);
    }
}

public partial class RegisterViewModel(IAuthService auth, IAppNavigator nav) : LoadableViewModel
{
    [ObservableProperty] private string fullName = "";
    [ObservableProperty] private string email = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string confirmPassword = "";

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (Password != ConfirmPassword) { ErrorMessage = "The passwords don't match."; return; }
        if (await RunAsync(() => auth.RegisterAsync(FullName, Email, Password))) nav.GoTo(AppRole.Member);
    }

    [RelayCommand] private void BackToSignIn() => nav.GoToLogin();
}

public partial class PackagesViewModel(IMembershipService svc, IAppNavigator nav) : LoadableViewModel
{
    public ObservableCollection<MembershipPackage> Packages { get; } = new();
    [ObservableProperty] private string? discountCode;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.ListPackagesAsync, rows => Fill(Packages, rows));

    // avail creates a pending QR payment; paying it and sending the proof happens on My membership
    [RelayCommand]
    private async Task AvailAsync(MembershipPackage package)
    {
        Notice = null;
        if (!await RunAsync(() => svc.AvailAsync(package.Id, DiscountCode), _ => { })) return;
        DiscountCode = null;
        nav.GoToMembership();
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

    /// <summary>The QR payment the member still has to pay and send proof for, if any.</summary>
    [ObservableProperty] private PaymentItem? payNow;
    [ObservableProperty] private string referenceNo = "";
    [ObservableProperty] private string? accountName;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasQr))] private string? qrUrl;
    public bool HasQr => QrUrl is not null;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        var waiting = false;
        await RunAsync(svc.MyPaymentsAsync, rows =>
        {
            Fill(Payments, rows);
            PayNow = rows.FirstOrDefault(p => p.NeedsProof);
            waiting = PayNow is not null || rows.Any(p => p.AwaitingReview);
        });
        await RunAsync(svc.MyMembershipsAsync, rows => { Fill(Memberships, rows); UpdateHero(rows, waiting); });
        if (PayNow is not null)
        {
            await RunAsync(svc.GymAsync, g => AccountName = g.QrAccountName);
            await RunAsync(svc.QrUrlAsync, url => QrUrl = url);
        }
    }

    void UpdateHero(IReadOnlyList<MembershipRow> rows, bool waitingOnPayment)
    {
        var active = rows.FirstOrDefault(r => r.Status == Status.Active);
        var expired = rows.FirstOrDefault(r => r.Status == Status.Expired);
        if (active is { DaysLeft: { } left })
        {
            HeroNumber = left.ToString();
            HeroCaption = left == 1 ? $"day left on {active.PackageName}" : $"days left on {active.PackageName}";
            LaneFraction = Math.Clamp((double)left / Math.Max(1, active.DurationDays), 0, 1);
        }
        else if (waitingOnPayment)
        { HeroNumber = "–"; HeroCaption = PayNow is not null ? "Pay by QR and send your proof to start your membership." : "Waiting for staff to verify your payment."; LaneFraction = 0; }
        else if (expired is not null)
        { HeroNumber = "0"; HeroCaption = $"{expired.PackageName} has expired. Renew to get back in."; LaneFraction = 0; }
        else
        { HeroNumber = "–"; HeroCaption = "Pick a package to get started."; LaneFraction = 0; }
    }

    [RelayCommand]
    private async Task RenewAsync(MembershipRow row)
    {
        Notice = null;
        if (!await RunAsync(() => svc.RenewAsync(row.Id, DiscountCode), _ => { })) return;
        DiscountCode = null;
        Notice = "Renewal payment created. Pay by QR below, then send your proof.";
        await LoadAsync();
    }

    // the reference number from the e-wallet receipt, plus a screenshot of it
    [RelayCommand]
    private async Task SendProofAsync()
    {
        Notice = null;
        if (PayNow is not { } pay) return;
        if (string.IsNullOrWhiteSpace(ReferenceNo)) { ErrorMessage = "Enter the reference number from your e-wallet receipt."; return; }
        if (await picker.PickAsync() is not { } file) { ErrorMessage = "Choose a screenshot of your payment to send."; return; }
        if (!await RunAsync(() => svc.SubmitProofAsync(pay.Id, ReferenceNo, file.FileName, file.Data))) return;
        ReferenceNo = "";
        Notice = "Sent. The front desk will check it against the gym's account.";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task PayAgainAsync(PaymentItem item)
    {
        Notice = null;
        if (!await RunAsync(() => svc.PayAgainAsync(item), _ => { })) return;
        Notice = "New payment created. Pay by QR below, then send your proof.";
        await LoadAsync();
    }
}

/// <summary>Every amenity with its live status, for members and coaches. Members can save an amenity to be alerted when it changes.</summary>
public partial class AmenityBoardViewModel(IAmenityService svc) : LoadableViewModel
{
    public ObservableCollection<AmenityCard> Cards { get; } = new();
    [ObservableProperty] private bool canFollow;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.BoardAsync, board => { Fill(Cards, board.Cards); CanFollow = board.CanFollow; });

    [RelayCommand]
    private async Task ToggleFollowAsync(AmenityCard card)
    { if (await RunAsync(() => svc.SetFollowedAsync(card.Amenity.Id, !card.Followed))) await LoadAsync(); }
}

public partial class CoachesViewModel(ICoachService svc, IMessageService messages, IAppNavigator nav) : LoadableViewModel
{
    public ObservableCollection<CoachCard> Cards { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.ListCoachesAsync, rows => Fill(Cards, rows));

    [RelayCommand]
    private async Task HireAsync(CoachCard card)
    { if (await RunAsync(() => svc.HireAsync(card.Coach.UserId))) await LoadAsync(); }

    [RelayCommand]
    private async Task EndHireAsync(CoachCard card)
    { if (card.HireId is { } id && await RunAsync(() => svc.EndHireAsync(id))) await LoadAsync(); }

    [RelayCommand]
    private void Message(CoachCard card) { messages.Pending = (card.Coach.UserId, null); nav.GoToMessages(); }
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

    // no live updates without a database: this reloads the threads and the open conversation
    [RelayCommand]
    private async Task RefreshAsync() { await LoadAsync(); await ShowAsync(); }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Selected is not { } t) return;
        if (await RunAsync(() => svc.SendAsync(t.CoachId, t.MemberId, Draft))) { Draft = ""; await ShowAsync(); }
    }
}

public partial class NotificationsViewModel : LoadableViewModel, IDisposable
{
    readonly INotificationService svc;
    readonly Action onChanged;

    public NotificationsViewModel(INotificationService svc)
    {
        this.svc = svc;
        onChanged = () => _ = LoadAsync();   // a change from anywhere (the bell, another page) redraws the open panel
        svc.Changed += onChanged;
    }

    public void Dispose() => svc.Changed -= onChanged;

    public ObservableCollection<UserNotification> Items { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread)), NotifyCanExecuteChangedFor(nameof(MarkAllReadCommand))]
    private int unreadCount;

    public bool HasUnread => UnreadCount > 0;

    public override Task OnAppearingAsync() => LoadAsync();

    int loadTicket;

    // The newest reload wins: a slow older answer must not bring back a dot for an alert that was just read.
    [RelayCommand]
    private Task LoadAsync()
    {
        var ticket = ++loadTicket;
        return RunAsync(svc.ListAsync, rows =>
        {
            if (ticket != loadTicket) return;
            Merge(rows);
            UnreadCount = rows.Count(n => n.ReadAt is null);
        });
    }

    // Same alerts as before: swap only the rows whose read time changed, so an open list keeps its scroll position.
    // A different set (a new alert arrived) rebuilds the list.
    void Merge(IReadOnlyList<UserNotification> rows)
    {
        if (Items.Count == rows.Count && Items.Select(i => i.Id).SequenceEqual(rows.Select(r => r.Id)))
        {
            for (var i = 0; i < rows.Count; i++) if (Items[i].ReadAt != rows[i].ReadAt) Items[i] = rows[i];
        }
        else Fill(Items, rows);
    }

    // the service raises Changed on success, which reloads the list
    [RelayCommand]
    private async Task MarkReadAsync(UserNotification n)
    { if (n.ReadAt is null) await RunAsync(() => svc.MarkReadAsync(n.Id)); }

    [RelayCommand(CanExecute = nameof(HasUnread))]
    private Task MarkAllReadAsync() => RunAsync(svc.MarkAllReadAsync);
}

public partial class SettingsViewModel(ISettingsService settings, IAuthService auth, IAppNavigator nav, IProfileService profile, IProofPicker picker) : LoadableViewModel
{
    public string[] Themes { get; } = ["system", "light", "dark"];
    [ObservableProperty] private string theme = "system";
    [ObservableProperty] private bool mustChange;
    [ObservableProperty] private string letters = "?";
    [ObservableProperty] private string? avatarUrl;

    public override async Task OnAppearingAsync()
    {
        await LoadAsync();
        await RunAsync(auth.MustChangePasswordAsync, m => MustChange = m);
        await LoadPhotoAsync();
    }

    async Task LoadPhotoAsync()
    {
        if ((await profile.MeAsync()).Value is not { } me) return;
        Letters = Initials.From(me.Name);
        AvatarUrl = (await profile.AvatarUrlAsync(me.AvatarPath)).Value;   // no link: the initials stay
    }

    [RelayCommand]
    private async Task ChangePhotoAsync()
    {
        Notice = null;
        if (await picker.PickAsync() is not { } file) return;
        if (!await RunAsync(() => profile.SetAvatarAsync(file.FileName, file.Data), _ => { })) return;
        await LoadPhotoAsync();
        Notice = "Picture updated.";
    }

    [RelayCommand] private Task LoadAsync() => RunAsync(settings.LoadAsync, s => Theme = s.Theme);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (await RunAsync(() => settings.SaveThemeAsync(Theme))) { nav.ApplyTheme(Theme); Notice = "Theme saved."; }
    }

    [ObservableProperty] private string currentPassword = "";
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string confirmPassword = "";

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        Notice = null;
        if (NewPassword != ConfirmPassword) { ErrorMessage = "The new passwords don't match."; return; }
        if (!await RunAsync(() => auth.ChangePasswordAsync(CurrentPassword, NewPassword))) return;
        CurrentPassword = NewPassword = ConfirmPassword = "";
        MustChange = false;
        Notice = "Password changed.";
    }

    [RelayCommand] private async Task SignOutAsync() { await auth.SignOutAsync(); nav.GoToLogin(); }
}

/// <summary>Feeds the bar at the top of every signed-in screen: your picture, the unread badge and Sign out.</summary>
public partial class HeaderViewModel : LoadableViewModel, IDisposable
{
    readonly IProfileService profile;
    readonly INotificationService notes;
    readonly IAuthService auth;
    readonly IAppNavigator nav;
    readonly Action onChanged;
    int refreshTicket;

    public HeaderViewModel(IProfileService profile, INotificationService notes, IAuthService auth, IAppNavigator nav)
    {
        (this.profile, this.notes, this.auth, this.nav) = (profile, notes, auth, nav);
        onChanged = () => _ = RefreshAsync();
        profile.AvatarChanged += onChanged;   // the services outlive every header, so each header must let go (Dispose)
        notes.Changed += onChanged;
    }

    /// <summary>Stops listening. Called on sign-out and when the shell is torn down.</summary>
    public void Dispose() { profile.AvatarChanged -= onChanged; notes.Changed -= onChanged; }

    [ObservableProperty] private string letters = "?";
    [ObservableProperty] private string? avatarUrl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText), nameof(AlertsLabel))] private int unreadCount;

    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public string AlertsLabel => HasUnread ? $"Alerts, {UnreadCount} unread" : "Alerts";

    public override Task OnAppearingAsync() => RefreshAsync();

    /// <summary>Reloads quietly: a bar that cannot reach the server keeps what it had and shows no error. The newest refresh wins.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var ticket = ++refreshTicket;
        var me = (await profile.MeAsync()).Value;
        var link = me is null ? null : await profile.AvatarUrlAsync(me.AvatarPath);
        var unread = await notes.UnreadCountAsync();
        if (ticket != refreshTicket) return;   // a newer refresh started while this one waited; its answer is the one to show
        if (me is not null) { Letters = Initials.From(me.Name); if (link is { Ok: true }) AvatarUrl = link.Value; }
        if (unread.Ok) UnreadCount = unread.Value;
    }

    [RelayCommand] private async Task SignOutAsync() { await auth.SignOutAsync(); Dispose(); nav.GoToLogin(); }
}
