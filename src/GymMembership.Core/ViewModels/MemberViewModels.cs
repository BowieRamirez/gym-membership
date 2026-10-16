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

    /// <summary>Called by the page each time it goes away.</summary>
    public virtual void OnDisappearing() { }

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

    /// <summary>
    /// Brings a list up to date without rebuilding it. Same rows in the same order: only the rows that changed are swapped, so an open list
    /// keeps its scroll position. A different set of rows rebuilds the list.
    /// </summary>
    protected static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> rows, Func<T, object> key, Func<T, T, bool> same)
    {
        if (target.Count == rows.Count && target.Select(key).SequenceEqual(rows.Select(key)))
        {
            for (var i = 0; i < rows.Count; i++) if (!same(target[i], rows[i])) target[i] = rows[i];
        }
        else Fill(target, rows);
    }

    /// <summary>
    /// What a page that refreshes itself every few seconds calls. By default it is the same load as opening the page; a page with an error line
    /// or a form open overrides it with a quiet version that never clears them and never shows a new error.
    /// </summary>
    public virtual Task AutoRefreshAsync() => OnAppearingAsync();
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

    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;   // a load or a tap is already running
        var board = await svc.BoardAsync();
        if (!board.Ok) return;   // stay quiet: the next tick tries again
        Sync(Cards, board.Value!.Cards, c => c.Amenity.Id, (a, b) => a.SameAs(b));
        CanFollow = board.Value.CanFollow;
    }

    [RelayCommand]
    private async Task ToggleFollowAsync(AmenityCard card)
    { if (await RunAsync(() => svc.SetFollowedAsync(card.Amenity.Id, !card.Followed))) await LoadAsync(); }
}

public partial class CoachesViewModel(ICoachService svc, IChatService messages, IAppNavigator nav) : LoadableViewModel
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

/// <summary>A conversation row: the thread plus its picture, which is looked up after the list is shown.</summary>
public partial class ThreadItem(ChatThread thread) : ObservableObject
{
    public ChatThread Thread { get; } = thread;
    [ObservableProperty] private string? avatarUrl;
    [ObservableProperty] private bool isOpen;   // the row of the conversation on screen
    [ObservableProperty] private bool isOnline; // the other person is in the app right now (set only where the thread allows it)
    public string Letters => Initials.From(Thread.WithName);

    /// <summary>Staff only: who has taken a member's front desk conversation, and whether it is closed.</summary>
    public string Subtitle => !Thread.IsSupport || Thread.WithId is null ? ""
        : (Thread.ClaimedByName is { } n ? $"Taken by {n}" : "Not taken yet") + (Thread.Status == "closed" ? ", closed" : "");
}

public partial class MessengerViewModel(IChatService chat, IProfileService profile, IAuthService auth, IProofPicker picker, IRealtimeChat? live = null) : LoadableViewModel
{
    List<ChatThread> all = [];
    string? myId;
    int openTicket, listTicket, searchTicket;
    readonly Dictionary<string, string> drafts = new();   // what was half typed in each conversation you left
    readonly IRealtimeChat rt = live ?? NoRealtime.Instance;
    bool listening, visible;   // visible: the page is on screen, so a connection that finishes later is still wanted
    int changeTicket, typingTicket;
    DateTime lastTypingSent = DateTime.MinValue;
    bool restoringDraft;
    IReadOnlySet<string> online = new HashSet<string>();   // ids of everyone online right now, as Realtime last said

    public ObservableCollection<ThreadItem> Threads { get; } = new();
    public ObservableCollection<ChatBubble> Bubbles { get; } = new();
    public ObservableCollection<ChatSearchHit> Hits { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowChat), nameof(ShowList))] private bool isCompact;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowChat), nameof(ShowList))] private bool showingChat;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChat), nameof(CanSend), nameof(ReadOnly), nameof(CanTake), nameof(CanRelease), nameof(CanClose), nameof(CanReopen), nameof(StatusLine))] private ThreadItem? selected;
    [ObservableProperty] private string draft = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPhoto), nameof(PhotoName))] private (string Name, byte[] Data)? pendingPhoto;   // chosen, not sent yet
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private string tab = "coach";
    [ObservableProperty] private string filter = "open";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSearching))] private string searchText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanTake), nameof(CanRelease), nameof(CanClose), nameof(CanReopen))] private bool hasFilters;
    [ObservableProperty] private bool hasTabs;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CoachTabText))] private int coachTabBadge;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StaffTabText))] private int staffTabBadge;

    /// <summary>On a phone the list and the chat take turns; on a desktop they sit side by side.</summary>
    public bool ShowList => !IsCompact || !ShowingChat;
    public bool ShowChat => !IsCompact || ShowingChat;
    public bool HasChat => Selected is not null;
    public bool IsSearching => SearchText.Trim().Length >= 1;
    public bool CanSend => Selected?.Thread.CanSend == true;
    /// <summary>A conversation is open but nobody can add to it now (the hire ended, or no active plan): history only.</summary>
    public bool ReadOnly => Selected is not null && !CanSend;
    public bool HasHits => Hits.Count > 0;
    public bool HasPhoto => PendingPhoto is not null;
    public string PhotoName => PendingPhoto?.Name ?? "";
    public string CoachTabText => CoachTabBadge > 0 ? $"Coach ({CoachTabBadge})" : "Coach";   // the count is in words, not only a coloured dot
    public string StaffTabText => StaffTabBadge > 0 ? $"Staff ({StaffTabBadge})" : "Staff";
    public bool CanTake => HasFilters && Selected?.Thread is { IsSupport: true, ClaimedBy: null };
    public bool CanRelease => HasFilters && Selected?.Thread is { IsSupport: true, ClaimedBy: not null } t && t.ClaimedBy == myId;
    public bool CanClose => HasFilters && Selected?.Thread is { IsSupport: true, Status: "open" };
    public bool CanReopen => HasFilters && Selected?.Thread is { IsSupport: true, Status: "closed" };
    public string EmptyListText => HasFilters ? "No conversations here." : Tab == "coach" && HasTabs ? "You have no coach yet. Hire one from the Coaches page to start a conversation." : "";

    // ---- live updates. Every Realtime event is a hint; the answer always comes from the server through AutoRefreshAsync.
    /// <summary>Realtime calls back on its own thread, and the lists must change on the UI thread. The page sets this; with none (tests) the work runs where it is.</summary>
    public SynchronizationContext? UiContext { get; set; }
    public TimeSpan ChangeDelay { get; set; } = TimeSpan.FromMilliseconds(300);   // a burst of signals becomes one reload
    /// <summary>How often the page looks again by itself: rarely while Realtime is connected, as before when not.</summary>
    public TimeSpan RefreshEvery => rt.Connected ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(6);
    public TimeSpan TypingHold { get; set; } = TimeSpan.FromSeconds(4);    // "typing…" goes away this long after the last ping
    public TimeSpan TypingEvery { get; set; } = TimeSpan.FromSeconds(2);   // at most one ping this often while you type
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusLine))] private bool otherTyping;
    /// <summary>Under the name in the chat header: "typing…", else "Online", else "Active 2 h ago", else nothing.</summary>
    public string StatusLine => OtherTyping ? "typing…" : PresenceText;
    string PresenceText => Selected?.Thread is { ShowsPresence: true } t
        ? (online.Contains(t.WithId!) ? "Online" : ActiveAgo.Text(t.WithLastSeen, DateTime.UtcNow))
        : "";

    // ---- loading
    public override async Task OnAppearingAsync()
    {
        visible = true;
        if (auth.UserId != myId)   // somebody else signed in: nothing of the last person stays on screen
        {
            var hadSomeone = myId is not null;
            Reset();
            if (hadSomeone) StopLive();   // the last person's connection, if any, goes
        }
        await LoadThreadsAsync(first: true);
        if (Selected is { } item) await ShowAsync(item, markRead: ShowChat);
        _ = StartLiveAsync();   // not awaited: connecting can take a while, and the page must not wait for it before its own timer starts
    }

    public override void OnDisappearing() { visible = false; StopLive(); }

    async Task StartLiveAsync()
    {
        var who = myId;
        if (who is null || !visible) return;   // not signed in, or the page was left while it was loading
        if (!listening)
        {
            rt.Changed += OnRealtimeChanged; rt.ConnectionChanged += OnRealtimeConnection; rt.Typing += OnRealtimeTyping; rt.OnlineChanged += OnRealtimeOnline;
            listening = true;
        }
        try
        {
            await rt.StartAsync(who);
            if (!visible || auth.UserId != who) { await StopQuietlyAsync(); return; }   // the page was left (or someone else signed in) while it connected
            if (Selected is { } open) await rt.WatchAsync(open.Thread.Key);
        }
        catch { /* Realtime is a bonus: without it the timer keeps the page fresh and nobody needs to hear about it */ }
        OnPropertyChanged(nameof(RefreshEvery));
    }

    void Unsubscribe()
    {
        if (!listening) return;
        rt.Changed -= OnRealtimeChanged; rt.ConnectionChanged -= OnRealtimeConnection; rt.Typing -= OnRealtimeTyping; rt.OnlineChanged -= OnRealtimeOnline;
        listening = false;
    }

    void StopLive()
    {
        Unsubscribe();
        changeTicket++;   // a reload still waiting for its pause is dropped
        StopTyping();
        online = new HashSet<string>(); ApplyOnline();   // nobody is shown as online while we are not listening
        _ = StopQuietlyAsync();
    }

    void OnRealtimeOnline(IReadOnlySet<string> ids) => Post(() => { online = ids; ApplyOnline(); });

    // the dot and the header text, for the people whose thread allows it
    void ApplyOnline()
    {
        foreach (var item in Threads) item.IsOnline = item.Thread is { ShowsPresence: true } t && online.Contains(t.WithId!);
        OnPropertyChanged(nameof(StatusLine));
    }

    void OnRealtimeTyping(string key) => Post(() =>
    {
        if (Selected?.Thread.Key != key) return;   // typing in a conversation that is not on screen
        OtherTyping = true;
        _ = HideTypingLaterAsync();
    });

    async Task HideTypingLaterAsync()
    {
        var ticket = ++typingTicket;
        await Task.Delay(TypingHold);
        if (ticket == typingTicket) OtherTyping = false;
    }

    void StopTyping() { typingTicket++; OtherTyping = false; }

    // what you type is a ping to the other side, at most one in TypingEvery; text put back from a saved draft is not typing
    partial void OnDraftChanged(string value)
    {
        if (restoringDraft || !listening || string.IsNullOrEmpty(value) || Selected is not { } item || !item.Thread.CanSend) return;
        var now = DateTime.UtcNow;
        if (now - lastTypingSent < TypingEvery) return;
        lastTypingSent = now;
        _ = SendTypingQuietlyAsync(item.Thread.Key);
    }

    async Task SendTypingQuietlyAsync(string key) { try { await rt.SendTypingAsync(key); } catch { /* a missed ping is nothing */ } }
    async Task WatchQuietlyAsync(string? key) { try { await rt.WatchAsync(key); } catch { /* typing just will not show */ } }

    async Task StopQuietlyAsync() { try { await rt.StopAsync(); } catch { /* already gone */ } }

    void Post(Action work) { if (UiContext is { } ui) ui.Post(_ => work(), null); else work(); }
    void OnRealtimeConnection() => Post(() => OnPropertyChanged(nameof(RefreshEvery)));
    void OnRealtimeChanged() => Post(() => _ = ReloadSoonAsync());

    async Task ReloadSoonAsync()
    {
        if (auth.UserId != myId) { Unsubscribe(); return; }   // a page left behind by a sign-out: it is not this person's any more
        var ticket = ++changeTicket;
        await Task.Delay(ChangeDelay);
        if (ticket == changeTicket && listening) await AutoRefreshAsync();   // the same quiet reload the timer does
    }

    void Reset()
    {
        myId = auth.UserId; all = [];
        openTicket++; listTicket++; searchTicket++;   // an answer still on its way for the last person is dropped when it lands
        Threads.Clear(); Bubbles.Clear(); SetHits([]);
        Selected = null; drafts.Clear(); Draft = ""; PendingPhoto = null; SearchText = ""; HasMore = false; ShowingChat = false;
        Tab = "coach"; Filter = "open"; HasTabs = false; HasFilters = false; CoachTabBadge = 0; StaffTabBadge = 0;
        ErrorMessage = null;
    }

    async Task LoadThreadsAsync(bool first)
    {
        var ticket = ++listTicket;
        var result = await chat.ThreadsAsync();
        if (!result.Ok) { if (first) ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        if (ticket != listTicket) return;   // a newer load is on its way
        all = result.Value!.ToList();
        // a member has their own "Front desk" thread and gets the Coach and Staff tabs; staff see member threads and get the Open, Mine and Closed filters; a coach gets neither
        var member = all.Any(t => t.IsSupport && t.WithName == "Front desk");
        HasTabs = member;
        HasFilters |= !member && all.Any(t => t.IsSupport);   // once on, stays on, so an emptied inbox keeps its filters
        CoachTabBadge = all.Where(t => !t.IsSupport).Sum(t => t.Unread);
        StaffTabBadge = all.Where(t => t.IsSupport).Sum(t => t.Unread);
        _ = chat.MarkDeliveredAsync();   // what was sent to you is at least delivered once your list has loaded
        Rebuild();
        if (first) PickInitial();
        _ = ResolveAvatarsAsync();
    }

    // what the list shows now, from what the server said and the tab, filter and search box. Only changed rows are swapped.
    void Rebuild()
    {
        IEnumerable<ChatThread> shown = HasFilters
            ? all.Where(t => t.IsSupport && Filter switch { "mine" => t.ClaimedBy == myId && t.Status == "open", "closed" => t.Status == "closed", _ => t.Status == "open" })
            : HasTabs ? all.Where(t => t.IsSupport == (Tab == "support")) : all;
        if (IsSearching) shown = shown.Where(t => t.WithName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));

        ThreadItem Item(ChatThread t)
        {
            var old = Threads.FirstOrDefault(x => x.Thread.Key == t.Key);
            return old is not null && Same(old.Thread, t) ? old : new ThreadItem(t) { AvatarUrl = old?.AvatarUrl };
        }
        Sync(Threads, shown.Select(Item).ToList(), x => x.Thread.Key, ReferenceEquals);

        // the open conversation follows the server's latest word about it (unread count, who took it, closed)
        if (Selected is { } open && all.FirstOrDefault(t => t.Key == open.Thread.Key) is { } fresh && !Same(open.Thread, fresh))
            Selected = Threads.FirstOrDefault(x => x.Thread.Key == fresh.Key) ?? new ThreadItem(fresh) { AvatarUrl = open.AvatarUrl };
        OnPropertyChanged(nameof(EmptyListText));
        ApplyOnline();   // rows built by this reload get their dot
    }

    static bool Same(ChatThread a, ChatThread b) => a.LastBody == b.LastBody && a.LastAt == b.LastAt && a.Unread == b.Unread && a.Status == b.Status
        && a.ClaimedBy == b.ClaimedBy && a.CanSend == b.CanSend && a.WithName == b.WithName && a.WithAvatar == b.WithAvatar && a.WithLastSeen == b.WithLastSeen;

    void PickInitial()
    {
        var want = chat.Pending; chat.Pending = null;
        if (want is { } w && all.FirstOrDefault(t => !t.IsSupport && t.CoachId == w.CoachId && (w.MemberId is null || t.MemberId == w.MemberId)) is { } hit)
        {
            Tab = "coach"; Rebuild();
            Selected = Threads.FirstOrDefault(x => x.Thread.Key == hit.Key);
            ShowingChat = true;   // on a phone, straight into that chat
            return;
        }
        if (Selected is null || all.All(t => t.Key != Selected.Thread.Key)) Selected = IsCompact ? null : Threads.FirstOrDefault();
    }

    async Task ResolveAvatarsAsync()
    {
        foreach (var item in Threads.Where(t => t.AvatarUrl is null && t.Thread.WithAvatar is not null).ToList())
            item.AvatarUrl = (await profile.AvatarUrlAsync(item.Thread.WithAvatar)).Value;
    }

    // ---- one conversation. The newest request wins: a slow answer for a conversation you have left is dropped.
    async Task ShowAsync(ThreadItem item, bool markRead)
    {
        var ticket = ++openTicket;
        var page = await chat.MessagesAsync(item.Thread);
        if (ticket != openTicket) return;
        if (!page.Ok) { ErrorMessage = ErrorText.For(page.Error, page.Message); return; }
        Bubbles.Clear();
        foreach (var b in page.Value!.Bubbles) Bubbles.Add(b);
        HasMore = page.Value.HasMore;
        if (markRead) await MarkOpenReadAsync(item);
    }

    async Task MarkOpenReadAsync(ThreadItem item)
    {
        var hadUnread = item.Thread.Unread > 0;
        await chat.MarkReadAsync(item.Thread);
        if (hadUnread) await LoadThreadsAsync(first: false);   // the badge in the list goes away
    }

    [RelayCommand]
    private async Task OpenAsync(ThreadItem item)
    {
        ErrorMessage = null;
        Selected = item;
        ShowingChat = true;
        Bubbles.Clear(); HasMore = false;
        await ShowAsync(item, markRead: true);
    }

    [RelayCommand]
    private async Task OpenHitAsync(ChatSearchHit hit)
    {
        SearchText = "";
        var thread = all.FirstOrDefault(t => t.Kind == hit.Kind && t.CoachId == hit.CoachId && t.MemberId == hit.MemberId);
        if (thread is null) return;
        if (HasTabs) Tab = thread.IsSupport ? "support" : "coach";
        if (HasFilters) Filter = thread.Status == "closed" ? "closed" : "open";   // show the list the conversation is in
        Rebuild();
        if (Threads.FirstOrDefault(x => x.Thread.Key == thread.Key) is { } item) await OpenAsync(item);
    }

    [RelayCommand] private void Back() => ShowingChat = false;

    [RelayCommand]
    private async Task LoadEarlierAsync()
    {
        if (Selected is not { } item || Bubbles.Count == 0) return;
        var page = await chat.MessagesAsync(item.Thread, Bubbles[0].Line.MessageId);
        if (!page.Ok || Selected?.Thread.Key != item.Thread.Key) return;
        var have = Bubbles.Select(b => b.Line.MessageId).ToHashSet();
        var add = page.Value!.Bubbles.Where(b => !have.Contains(b.Line.MessageId)).ToList();
        for (var i = add.Count - 1; i >= 0; i--) Bubbles.Insert(0, add[i]);
        HasMore = page.Value.HasMore;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Selected is not { } item || (string.IsNullOrWhiteSpace(Draft) && PendingPhoto is null)) return;
        var sent = Draft; var photo = PendingPhoto;
        var ok = photo is { } p
            ? await RunAsync(() => chat.SendPhotoAsync(item.Thread, p.Name, p.Data, sent))
            : await RunAsync(() => chat.SendAsync(item.Thread, sent));
        if (!ok) return;   // the caption and the photo stay, with the error line
        drafts.Remove(item.Thread.Key);
        if (Selected?.Thread.Key == item.Thread.Key)   // not if more was typed, or you moved on, while it was sending
        {
            if (Draft == sent) Draft = "";
            if (PendingPhoto == photo) PendingPhoto = null;
        }
        await MergeLatestAsync(item);
        await LoadThreadsAsync(first: false);
    }

    [RelayCommand]
    private async Task AttachAsync()
    {
        ErrorMessage = null;
        if (await picker.PickAsync() is not { } file) return;   // cancelled
        if (!ProfileService.IsAcceptableImage(file.FileName, file.Data)) { ErrorMessage = ErrorText.For(AppErrorKind.Validation, "bad_image"); return; }
        PendingPhoto = file;
    }

    [RelayCommand] private void RemovePhoto() => PendingPhoto = null;

    // ---- tabs, filters, search
    [RelayCommand]
    private async Task ShowTabAsync(string tab)
    {
        Tab = tab; Selected = null; Bubbles.Clear(); HasMore = false;
        Rebuild();
        if (!IsCompact && Threads.FirstOrDefault() is { } first) await OpenAsync(first);
    }

    [RelayCommand] private Task ShowFilterAsync(string filter) { Filter = filter; Rebuild(); return Task.CompletedTask; }

    partial void OnSearchTextChanged(string value)
    {
        Rebuild();
        var ticket = ++searchTicket;
        if (value.Trim().Length < 2) { SetHits([]); return; }
        _ = SearchLaterAsync(value, ticket);
    }

    async Task SearchLaterAsync(string text, int ticket)
    {
        await Task.Delay(350);   // wait for a pause in typing
        if (ticket != searchTicket) return;
        var result = await chat.SearchAsync(text);
        // a person whose name matches is already in the list above; this section is for what was said in messages
        if (ticket == searchTicket && result.Ok) SetHits(result.Value!.Where(h => h.Snippet?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) == true));
    }

    void SetHits(IEnumerable<ChatSearchHit> rows) { Fill(Hits, rows); OnPropertyChanged(nameof(HasHits)); }

    // the row of the open conversation is marked, so the list shows where you are
    partial void OnSelectedChanged(ThreadItem? oldValue, ThreadItem? newValue)
    {
        if (oldValue is not null) oldValue.IsOpen = false;
        if (newValue is not null) newValue.IsOpen = true;
        if (oldValue?.Thread.Key == newValue?.Thread.Key) return;   // the same conversation, just refreshed
        if (oldValue is not null) drafts[oldValue.Thread.Key] = Draft;
        restoringDraft = true;
        Draft = newValue is null ? "" : drafts.GetValueOrDefault(newValue.Thread.Key, "");
        restoringDraft = false;
        PendingPhoto = null;   // a half-chosen photo is dropped when you move to another conversation
        StopTyping();
        if (listening) _ = WatchQuietlyAsync(newValue?.Thread.Key);
    }

    // ---- front desk
    [RelayCommand] private Task TakeAsync() => ClaimAsync(true);
    [RelayCommand] private Task ReleaseAsync() => ClaimAsync(false);
    [RelayCommand] private Task CloseChatAsync() => SetOpenAsync(false);
    [RelayCommand] private Task ReopenAsync() => SetOpenAsync(true);

    async Task ClaimAsync(bool claim)
    {
        if (Selected is { } item && await RunAsync(() => chat.ClaimAsync(item.Thread, claim))) await LoadThreadsAsync(first: false);
    }

    async Task SetOpenAsync(bool open)
    {
        if (Selected is { } item && await RunAsync(() => chat.SetOpenAsync(item.Thread, open))) await LoadThreadsAsync(first: false);
    }

    // ---- quiet refresh: new messages arrive and the list updates; a draft, an error line and the scroll position are left alone
    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
        await LoadThreadsAsync(first: false);
        if (Selected is not { } item) return;
        await MergeLatestAsync(item);
        if (ShowChat && Selected is { } open && open.Thread.Unread > 0) await MarkOpenReadAsync(open);
    }

    // adds what is new at the bottom and swaps a message whose status moved on (Sent, Delivered, Read); nothing is rebuilt
    async Task MergeLatestAsync(ThreadItem item)
    {
        var ticket = openTicket;
        var page = await chat.MessagesAsync(item.Thread);
        if (!page.Ok || ticket != openTicket || Selected?.Thread.Key != item.Thread.Key) return;
        if (Bubbles.Count == 0) HasMore = page.Value!.HasMore;
        var known = Bubbles.ToDictionary(b => b.Line.MessageId);
        foreach (var b in page.Value!.Bubbles)
        {
            if (!known.TryGetValue(b.Line.MessageId, out var old)) { Bubbles.Add(b); if (!b.Mine) StopTyping(); }   // they sent it, so they are no longer typing
            else if (old.Status != b.Status || old.PhotoUrl != b.PhotoUrl) Bubbles[Bubbles.IndexOf(old)] = b;   // Sent became Read, or a photo link was renewed
        }
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
            Sync(Items, rows, n => n.Id, (a, b) => a.ReadAt == b.ReadAt);
            UnreadCount = rows.Count(n => n.ReadAt is null);
        });
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
