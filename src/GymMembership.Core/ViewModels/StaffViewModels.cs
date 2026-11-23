using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

// ---------- front desk ----------

/// <summary>One tile on the front desk home. Route says which screen it opens (a StaffShell route).</summary>
public sealed record HomeTile(string Route, string Icon, string Title, string Count, string Detail, bool NeedsAttention)
{
    /// <summary>What a screen reader says: the count belongs with the title, not only in the picture.</summary>
    public string Description => $"{Title}: {Count}";
}

/// <summary>The front desk home: the four tiles, then the payments waiting and the coaches in the gym (Phase 4b).
/// Without the payment and coach services (older tests) it is the tiles alone.</summary>
public partial class HomeViewModel(IDeskHomeService svc, IPaymentStaffService? payments = null, ICoachService? coaches = null, ISessionService? sessions = null) : LoadableViewModel
{
    public ObservableCollection<HomeTile> Tiles { get; } = new();
    /// <summary>Desk bookings waiting for their cash (cashier plan Y7); a row opens the Cashier, where it is taken.</summary>
    public ObservableCollection<PaymentRow> ToVerify { get; } = new();
    public ObservableCollection<CoachPresenceRow> InGym { get; } = new();
    public ObservableCollection<TodayCoachSession> TodaySessions { get; } = new();
    public bool HasLists => payments is not null && coaches is not null;
    public bool HasSessions => sessions is not null;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!await RunAsync(svc.SummaryAsync, s => Show(s)) || !HasLists) return;
        var tasks = new List<Task>
        {
            RunAsync(payments!.PendingAsync, ShowPayments),
            RunAsync(coaches!.PresenceAsync, ShowCoaches),
        };
        if (sessions is not null) tasks.Add(RunAsync(sessions.TodayCoachSessionsAsync, ShowTodaySessions));
        await Task.WhenAll(tasks);
    }

    // Quiet: the error line is never touched and a failed look leaves the numbers as they were.
    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
        var result = await svc.SummaryAsync();
        if (!result.Ok) return;
        ErrorMessage = null;   // a look that works replaces the banner a failed first load left above these numbers
        Show(result.Value!);
        if (!HasLists) return;
        var pTask = payments!.PendingAsync();
        var cTask = coaches!.PresenceAsync();
        var tsTask = sessions is not null ? sessions.TodayCoachSessionsAsync() : null;
        await (tsTask is not null ? Task.WhenAll(pTask, cTask, tsTask) : Task.WhenAll(pTask, cTask));
        if (pTask.Result is { Ok: true } p) ShowPayments(p.Value!);
        if (cTask.Result is { Ok: true } c) ShowCoaches(c.Value!);
        if (tsTask?.Result is { Ok: true } ts) ShowTodaySessions(ts.Value!);
    }

    [RelayCommand]
    private async Task MarkCoachArrivedAsync(TodayCoachSession row)
    {
        if (sessions is null) return;
        Notice = null;
        if (!await RunAsync(() => sessions.MarkCoachArrivedAsync(row.SessionId))) return;
        Notice = $"{row.CoachName} marked arrived. Session is now In session.";
        await RunAsync(sessions.TodayCoachSessionsAsync, ShowTodaySessions);
    }

    [RelayCommand]
    private async Task CheckOutCoachAsync(CoachPresenceRow row)
    {
        Notice = null;
        if (!await Confirm.AskAsync($"Check {row.Name} out?", "Members will no longer see them as in the gym.", "Check out")) return;
        if (!await RunAsync(() => coaches!.CheckOutAsync(row.CoachId))) return;
        Notice = $"{row.Name} is checked out.";
        await RunAsync(coaches!.PresenceAsync, ShowCoaches);
    }

    void Show(DeskSummary s) => Sync(Tiles, Build(s), t => t.Route, (a, b) => a == b);
    void ShowPayments(IReadOnlyList<PaymentRow> rows) => Sync(ToVerify, rows, p => p.Payment.Id, (a, b) => a == b);
    void ShowCoaches(IReadOnlyList<CoachPresenceRow> rows) => Sync(InGym, rows.Where(r => r.InGym).ToList(), r => r.CoachId, (a, b) => a == b);
    void ShowTodaySessions(IReadOnlyList<TodayCoachSession> rows) => Sync(TodaySessions, rows, r => r.SessionId, (a, b) => a == b);

    public static IReadOnlyList<HomeTile> Build(DeskSummary s)
    {
        var amenityDetail = string.Join(", ", new[] { s.AmenitiesInUse > 0 ? $"{s.AmenitiesInUse} in use" : null, s.AmenitiesUnavailable > 0 ? $"{s.AmenitiesUnavailable} unavailable" : null }.OfType<string>());
        // 9.7: one highlighted figure (the page standard): unanswered chats.
        bool chats = s.UnreadMessages > 0 || s.UnclaimedChats > 0;
        return
        [
            new("checkin", "log-in", "Check-ins today", s.CheckInsToday.ToString(),
                s.CheckInsToday == 0 ? "No one has checked in yet" : "Tap to check someone in", false),
            new("amenities", "waves", "Amenities available", $"{s.AmenitiesAvailable} of {s.AmenitiesTotal}",
                amenityDetail.Length == 0 ? "All open" : amenityDetail, false),
            new("messages", "message-square", "Unread messages", s.UnreadMessages.ToString(),
                s.UnclaimedChats > 0 ? (s.UnclaimedChats == 1 ? "1 chat nobody has taken" : $"{s.UnclaimedChats} chats nobody has taken")
                : s.UnreadMessages > 0 ? "Open Messages to reply" : "No new messages", chats),
        ];
    }
}

/// <summary>Shared 300 ms debounced member suggestor for Cashier and DeskBookingViewModel: 2+ chars, newest request wins.</summary>
internal sealed class MemberSuggest(IAttendanceService members)
{
    CancellationTokenSource? _cts;
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(300);

    public void Cancel() => _cts?.Cancel();

    public async Task<(bool Applied, IReadOnlyList<MemberHit> Rows, string? Error)> RunAsync(string? query, bool immediate = false)
    {
        _cts?.Cancel();
        var q = query?.Trim() ?? "";
        if (q.Length < 2) return (true, Array.Empty<MemberHit>(), null);
        var cts = _cts = new CancellationTokenSource();
        if (!immediate && Delay > TimeSpan.Zero)
        {
            try { await Task.Delay(Delay, cts.Token); }
            catch (OperationCanceledException) { return (false, Array.Empty<MemberHit>(), null); }
        }
        var res = await members.FindMembersAsync(q);
        if (cts.IsCancellationRequested) return (false, Array.Empty<MemberHit>(), null);
        return res.Ok
            ? (true, res.Value!.Where(h => h.Status != "inactive").ToList(), null)
            : (true, Array.Empty<MemberHit>(), ErrorText.For(res.Error, res.Message));
    }
}

// front desk: cash and GCash at the counter, and walk-ins. Verified straight away with a receipt number
/// <summary>One thing for sale on the cashier's grid, and whether it is the one picked.</summary>
public partial class ItemOption(CounterItem item) : ObservableObject
{
    public CounterItem Item { get; } = item;
    public string Name => Item.Label;
    public string PriceText => Item.Price is { } p ? $"₱{p:N2}" : "";
    [ObservableProperty] private bool isPicked;
}

public partial class CashierViewModel(IPaymentStaffService svc, IAttendanceService members, IUrlOpener? opener = null, IMembershipService? gym = null, ISessionService? sessions = null) : LoadableViewModel
{
    public string[] Kinds { get; } = ["Membership", "Renewal", "Amenity", "Day pass"];   // cashier plan Y3: a walk-in buys a Day pass
    /// <summary>Cashier plan Y6: e-wallets at the desk go through Xendit's checkout (GCash, Maya or any bank app by QR Ph).</summary>
    public const string Online = "GCash / QR Ph";
    public string[] Methods { get; } = ["Cash", Online];   // owner, 2026-11-11: no bank transfer (0054)
    /// <summary>A desk online payment whose Xendit checkout is open on this PC, until it is paid.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaiting))] private Payment? waiting;
    public bool IsWaiting => Waiting is not null;
    public ObservableCollection<MemberHit> Hits { get; } = new();
    public ObservableCollection<ItemOption> Options { get; } = new();
    int itemsTicket;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWalkIn), nameof(NeedsMember), nameof(CanUseCode), nameof(OrderWho), nameof(OrderWhat), nameof(OrderHow), nameof(TotalText), nameof(TotalNote), nameof(ItemsEmptyText), nameof(HasMultipleDayPassOptions), nameof(DayPassWithCoach), nameof(OrderIneligibleText), nameof(HasOrderIneligible)), NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    private string kind = "Membership";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderHow), nameof(CashNeedsShift)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string method = "Cash";
    [ObservableProperty] private string search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWho), nameof(CanUseCode), nameof(ItemsEmptyText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private MemberHit? member;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWhat), nameof(TotalText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private CounterItem? item;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWho), nameof(CanUseCode), nameof(OrderIneligibleText), nameof(HasOrderIneligible)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string walkInName = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalNote))] private string discountCode = "";
    // owner fixes 2026-11-19 Q12 (0108): the code is checked as the desk types it (500 ms), and again when the member or item changes
    public TimeSpan CodeCheckDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalText), nameof(HasCodeCheck))] private CodeCheck? codeCheck;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasCodeCheck))] private string? codeCheckText;
    [ObservableProperty] private bool codeWorks;
    public bool HasCodeCheck => !string.IsNullOrWhiteSpace(CodeCheckText);
    CancellationTokenSource? _codeCheck;
    (string Member, string Kind, int Item, string Code)? _checked;
    partial void OnDiscountCodeChanged(string value) => _ = ScheduleCodeCheckAsync(false);
    partial void OnWalkInNameChanged(string value) => _ = ScheduleCodeCheckAsync(false);
    public Task CheckCodeNowAsync() => ScheduleCodeCheckAsync(true);
    async Task ScheduleCodeCheckAsync(bool immediate)
    {
        var code = DiscountCode?.Trim() ?? "";
        var key = !IsWalkIn && Member is { } m && Item is { } it && code.Length > 0 ? (m.MemberId, Kind, it.Id, code)
            : DayPassMember is { HasActiveMembership: false } dm && dayPassPackage is { } dp && code.Length > 0 ? (dm.MemberId, "Membership", dp.Id, code)
            : ((string Member, string Kind, int Item, string Code)?)null;
        if (immediate && key is not null && _checked == key) return;
        _codeCheck?.Cancel(); _checked = null;
        CodeCheck = null; CodeCheckText = null; CodeWorks = false;
        if (key is not { } k) return;
        var cts = _codeCheck = new CancellationTokenSource();
        if (!immediate && CodeCheckDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(CodeCheckDelay, cts.Token); } catch (OperationCanceledException) { return; }
        }
        var kind = k.Kind == "Amenity" ? "amenity" : k.Kind == "Renewal" ? "renewal" : "package";
        var res = await svc.CheckCodeAsync(k.Member, k.Code, kind, k.Item, k.Kind == "Renewal" ? Item?.Price : null);
        if (cts.IsCancellationRequested) return;
        _checked = k;
        if (!res.Ok) { CodeCheckText = ErrorText.For(res.Error, res.Message); return; }
        CodeCheck = res.Value; CodeWorks = res.Value!.Works;
        CodeCheckText = CodeCheckFeedback.Message(res.Value, kind == "amenity" ? "amenity" : "package");
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ItemsEmptyText))] private bool loadingItems;

    // F2 (0050): the cashier's own drawer shift. Cash needs an open one; GCash doesn't.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasShift), nameof(NeedsShift), nameof(ShiftSince), nameof(CountText), nameof(CloseNeedsNote), nameof(CashNeedsShift)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private CashShift? shift;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsShift))] private bool shiftChecked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StartNeedsNote))] private decimal startFloat;
    [ObservableProperty] private string startNote = "";
    [ObservableProperty] private bool isClosing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText), nameof(CloseNeedsNote))] private decimal? counted;
    [ObservableProperty] private string countedText = "";   // the box: an empty or unreadable count is no count, not the last number typed
    [ObservableProperty] private string closeNote = "";
    [ObservableProperty] private string closeReason = "";
    [RelayCommand] private void PickCloseReason(string reason) => CloseReason = reason;
    decimal defaultFloat;

    public bool HasShift => Shift is not null;
    public bool NeedsShift => ShiftChecked && Shift is null;
    public bool StartNeedsNote => StartFloat != defaultFloat;
    public string FloatNoteLabel => $"Why is it different from ₱{defaultFloat:N2}?";
    public string ShiftSince => Shift is { } s ? $"Since {s.OpenedAt.ToLocalTime():h:mm tt}" : "";
    public string CountText => Counted is { } c && Shift is { } s ? CashCount.Words(c - s.Expected) : "";
    public bool CloseNeedsNote => Counted is { } c && Shift is { } s && c != s.Expected;
    partial void OnCountedTextChanged(string value) =>
        Counted = decimal.TryParse(value.Replace("₱", "").Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var d) && d >= 0 ? d : null;
    /// <summary>Cash is chosen but no shift is open: Complete payment waits, and the order says why.</summary>
    public bool CashNeedsShift => Method == "Cash" && Shift is null;

    public bool IsWalkIn => Kind == "Day pass";
    public bool NeedsMember => !IsWalkIn;
    public bool CanUseCode => !IsWalkIn || DayPassMember is { HasActiveMembership: false };
    // cashier plan Y6: no reference to type; Xendit confirms an e-wallet payment

    // the order panel, in the words the cashier uses
    // cashier plan Y3: a Day pass may carry a member found by search, a typed name, or nothing (anonymous)
    // plan 2026-11-17 Z3: the name box is the one source; picking a member only fills it, so what the desk typed wins
    public string OrderWho => IsWalkIn ? DayPassName ?? "Day pass, no name" : Member?.Username ?? "Choose a customer";
    public string? OrderWhoStatus => Member?.DeskStatusText;
    public string? OrderWhoContact => Member?.ContactText;
    public bool HasChosenMember => Member is not null;
    public bool ShowChooseCustomerEmpty => !IsWalkIn && Member is null;
    string? DayPassName => string.IsNullOrWhiteSpace(WalkInName) ? null : WalkInName.Trim();
    public string OrderWhat => Item?.Label ?? (IsWalkIn ? dayPassPackage?.Label ?? "Day pass" : "Choose what they are paying for");
    decimal? dayPassPrice;   // the server charges it, this is what the desk reads out
    // 0098, 0125: Day pass packages (regular vs Coach included); a picked member whose name is still in the box buys it (a term until midnight), anyone else is a walk-in
    CounterItem? dayPassPackage;
    public ObservableCollection<ItemOption> DayPassOptions { get; } = new();
    public bool HasMultipleDayPassOptions => IsWalkIn && DayPassOptions.Count > 0;
    public bool DayPassWithCoach => IsWalkIn && dayPassPackage is { IncludesCoach: true };
    public ObservableCollection<FreeCoachSlot> DayPassCoaches { get; } = new();
    public ObservableCollection<SessionsViewModel.HourOption> DayPassHours { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderIneligibleText), nameof(HasOrderIneligible)), NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    private FreeCoachSlot? selectedDayPassCoach;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderIneligibleText), nameof(HasOrderIneligible)), NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    private SessionsViewModel.HourOption? selectedDayPassHour;
    [ObservableProperty] private bool loadingCoaches;
    public string DayPassCoachTitle => LoadingCoaches ? "Loading coaches…" : DayPassCoaches.Count == 0 ? "No coaches free today" : "Coach";
    (string? MemberId, string? WalkInName, string CoachId, int Hour, string Method)? _pendingDayPassCoach;
    partial void OnSelectedDayPassCoachChanged(FreeCoachSlot? value)
    {
        DayPassHours.Clear();
        if (value is null) { SelectedDayPassHour = null; return; }
        foreach (var h in value.FreeHours)
            DayPassHours.Add(new SessionsViewModel.HourOption(h, $"{DateTime.Today.AddHours(h):h tt} – {DateTime.Today.AddHours(h + 1):h tt}"));
        SelectedDayPassHour = DayPassHours.FirstOrDefault();
    }
    [RelayCommand]
    private void PickDayPassOption(ItemOption option)
    {
        dayPassPackage = option.Item;
        foreach (var o in DayPassOptions) o.IsPicked = o.Item.Id == option.Item.Id;
        OnPropertyChanged(nameof(OrderWhat)); OnPropertyChanged(nameof(CanUseCode)); OnPropertyChanged(nameof(TotalText)); OnPropertyChanged(nameof(TotalNote));
        OnPropertyChanged(nameof(DayPassWithCoach)); OnPropertyChanged(nameof(OrderIneligibleText)); OnPropertyChanged(nameof(HasOrderIneligible));
        RecordCommand.NotifyCanExecuteChanged();
        _ = ScheduleCodeCheckAsync(false);
        if (dayPassPackage.IncludesCoach) _ = LoadDayPassCoachesAsync();
    }
    async Task LoadDayPassCoachesAsync()
    {
        if (sessions is null) return;
        LoadingCoaches = true; OnPropertyChanged(nameof(DayPassCoachTitle));
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
            var res = await sessions.FreeCoachesAsync(today);
            if (!res.Ok) { ErrorMessage = ErrorText.For(res.Error, res.Message); return; }
            Fill(DayPassCoaches, res.Value!);
            SelectedDayPassCoach = DayPassCoaches.FirstOrDefault();
            OnPropertyChanged(nameof(OrderIneligibleText)); OnPropertyChanged(nameof(HasOrderIneligible));
            RecordCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            LoadingCoaches = false; OnPropertyChanged(nameof(DayPassCoachTitle));
        }
    }
    MemberHit? DayPassMember => IsWalkIn && dayPassPackage is not null && Member is { } m && DayPassName == m.Username ? m : null;
    public string TotalText => IsWalkIn
        ? CodeCheck is { Works: true, YouPay: { } dOff } ? $"₱{dOff:N2}"
          : (dayPassPackage?.Price ?? dayPassPrice) is { } d ? $"₱{d:N2}" : "Day pass"
        : CodeCheck is { Works: true, YouPay: { } off } ? $"₱{off:N2}" : Item?.Price is { } p ? $"₱{p:N2}" : "₱0.00";
    public string TotalNote => IsWalkIn ? dayPassPackage is null ? "The day pass price is set in Gym settings." : "The Day pass price is set on its package."
        : "";
    public string ItemsEmptyText => LoadingItems ? "Loading…"
        : Kind == "Renewal" && Member is null ? "Choose a member to see their memberships." : "Nothing to sell here yet.";
    /// <summary>The kind and the way of paying in words, so the order reads the same to someone who cannot see which button is filled.</summary>
    public string OrderHow => $"{Kind}, paid with {Method}";

    static string KindKey(string k) => k switch { "Renewal" => PayKind.Renewal, "Amenity" => PayKind.Amenity, "Day pass" => PayKind.WalkIn, _ => PayKind.Membership };

    public override Task OnAppearingAsync()
    {
        var tasks = new List<Task> { RefreshItemsAsync(), LoadShiftAsync() };
        if (gym is not null)
            tasks.Add(Task.Run(async () =>
            {
                if (await gym.GymAsync() is { Ok: true, Value: { } settings })
                { dayPassPrice = settings.WalkInPrice; OnPropertyChanged(nameof(TotalText)); }
            }));
        // a desk payment still waiting for Xendit comes back with its Check payment button (leaving the page used to lose it)
        if (Waiting is null)
            tasks.Add(RunAsync(svc.MyWaitingOnlineAsync, p => { Waiting = p; RecordCommand.NotifyCanExecuteChanged(); }));
        return Task.WhenAll(tasks);
    }

    // owner, 2026-11-18: only a payment waiting for Xendit is refreshed; the order being typed is left alone
    bool polling;
    public override async Task AutoRefreshAsync()
    {
        if (polling || IsBusy || Waiting is not { } pay) return;
        polling = true;
        try
        {
            var state = await DeskOnlineWait.PollAsync(svc, pay.Id);
            if (Waiting?.Id != pay.Id) return;   // Customer left was pressed meanwhile
            if (state == OnlineWait.Paid) await PaidAsync(pay.Id);
            else if (state == OnlineWait.Over)
            {
                Waiting = null; Notice = "That online payment expired or was cancelled, so nothing was recorded."; RecordCommand.NotifyCanExecuteChanged();
            }
        }
        finally { polling = false; }
    }

    Task<bool> LoadShiftAsync() => RunAsync(svc.ShiftAsync, state =>
    {
        defaultFloat = state.DefaultFloat; Shift = state.Open; ShiftChecked = true;
        if (Shift is null) StartFloat = state.DefaultFloat;
        OnPropertyChanged(nameof(StartNeedsNote)); OnPropertyChanged(nameof(FloatNoteLabel));
    });

    [RelayCommand]
    private async Task StartShiftAsync()
    {
        Notice = null;
        if (!await RunAsync(() => svc.OpenShiftAsync(StartFloat, StartNote), _ => { })) return;
        StartNote = "";
        await LoadShiftAsync();
        Notice = $"Shift started with a float of ₱{StartFloat:N2}.";
    }

    [RelayCommand] private void BeginClose() { IsClosing = true; CountedText = ""; Counted = null; CloseNote = ""; CloseReason = ""; Notice = null; }
    [RelayCommand] private void KeepShift() => IsClosing = false;

    [RelayCommand]
    private async Task EndShiftAsync()
    {
        Notice = null;
        if (Shift is not { } open) return;
        if (Counted is not { } count) { ErrorMessage = "Type the cash you counted in the drawer."; return; }
        if (!await Confirm.AskAsync("End your shift?", $"Expected ₱{open.Expected:N2}\nCounted ₱{count:N2}\n{CountText}", "End shift")) return;
        if (!await RunAsync(() => svc.CloseShiftAsync(count, CloseNote, CloseReason), _ => { })) return;
        var notice = count == open.Expected ? $"Shift ended. Your count matches the expected ₱{count:N2}." : $"Shift ended. Counted ₱{count:N2}: {CountText}.";
        IsClosing = false; CountedText = ""; Counted = null; CloseNote = ""; CloseReason = "";
        await LoadShiftAsync();
        Notice = notice;
    }

    // an item belongs to the kind (and member) it was listed for: change either and the pick goes
    bool _refreshingStatus;
    partial void OnKindChanged(string value)
    {
        _ = ScheduleCodeCheckAsync(false); Item = null;
        if (IsWalkIn && Member is { } m && string.IsNullOrWhiteSpace(WalkInName)) WalkInName = m.Username;
        ApplyFilter(hasQuery: (Search?.Trim().Length ?? 0) >= 2, err: null);
        OnPropertyChanged(nameof(ShowChooseCustomerEmpty));
        OnPropertyChanged(nameof(OrderIneligibleText)); OnPropertyChanged(nameof(HasOrderIneligible));
        RecordCommand.NotifyCanExecuteChanged();
        _ = RefreshItemsAsync();
    }   // a typed name is kept (final review)
    partial void OnMemberChanged(MemberHit? value)
    {
        OnPropertyChanged(nameof(OrderWhoStatus)); OnPropertyChanged(nameof(OrderWhoContact));
        OnPropertyChanged(nameof(HasChosenMember)); OnPropertyChanged(nameof(ShowChooseCustomerEmpty));
        OnPropertyChanged(nameof(OrderIneligibleText)); OnPropertyChanged(nameof(HasOrderIneligible));
        for (var i = 0; i < Hits.Count; i++)
        {
            var chosen = value is not null && Hits[i].MemberId == value.MemberId;
            if (Hits[i].IsChosen != chosen) Hits[i] = Hits[i] with { IsChosen = chosen };
        }
        RecordCommand.NotifyCanExecuteChanged();
        if (_refreshingStatus) return;   // D3: re-reading status updates the chip without dropping Item/code
        _ = ScheduleCodeCheckAsync(false); Item = null;
        if (IsWalkIn && value is not null) WalkInName = value.Username;
        if (Kind == "Renewal") _ = RefreshItemsAsync();
    }
    partial void OnItemChanged(CounterItem? value) { _ = ScheduleCodeCheckAsync(false); foreach (var o in Options) o.IsPicked = value is not null && o.Item.Id == value.Id; }

    async Task RefreshItemsAsync()
    {
        var ticket = ++itemsTicket;   // the newest request wins: a slow answer for a kind you have left is dropped
        // Cards listed for another kind or member must not stay tappable while the right ones load: a tap would send their item id with the new kind.
        Options.Clear(); Item = null; ErrorMessage = null;
        if (IsWalkIn)
        {
            LoadingItems = false;
            var packages = await svc.ItemsAsync(PayKind.Membership, null);
            if (ticket == itemsTicket && packages.Ok)
            {
                var dps = packages.Value!.Where(i => i.DayPass).OrderBy(i => i.IncludesCoach).ToList();
                Fill(DayPassOptions, dps.Select(i => new ItemOption(i)));
                dayPassPackage = dps.FirstOrDefault(i => dayPassPackage is not null && i.Id == dayPassPackage.Id) ?? dps.FirstOrDefault();
                foreach (var o in DayPassOptions) o.IsPicked = dayPassPackage is not null && o.Item.Id == dayPassPackage.Id;
                OnPropertyChanged(nameof(HasMultipleDayPassOptions)); OnPropertyChanged(nameof(DayPassWithCoach));
                OnPropertyChanged(nameof(CanUseCode)); OnPropertyChanged(nameof(TotalText)); OnPropertyChanged(nameof(TotalNote)); OnPropertyChanged(nameof(OrderWhat));
                OnPropertyChanged(nameof(OrderIneligibleText)); OnPropertyChanged(nameof(HasOrderIneligible));
                RecordCommand.NotifyCanExecuteChanged();
                if (dayPassPackage?.IncludesCoach == true) _ = LoadDayPassCoachesAsync();
            }
            return;
        }
        LoadingItems = true;
        var result = await svc.ItemsAsync(KindKey(Kind), Member?.MemberId);
        if (ticket != itemsTicket) return;   // a newer request owns the screen now; a stale answer, even a failed one, says nothing
        LoadingItems = false;
        if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        Fill(Options, result.Value!.Select(r => new ItemOption(r)));
    }

    /// <summary>Cashier plan Y1: search ran and nobody matched; the page says so.</summary>
    [ObservableProperty] private bool noMatch;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHiddenHint))] private string? hiddenHint;
    public bool HasHiddenHint => !string.IsNullOrWhiteSpace(HiddenHint) && !IsRegistering;
    readonly MemberSuggest _suggest = new(members);
    List<MemberHit> _allHits = new();
    bool _suppressSearch;
    public TimeSpan SearchDelay { get => _suggest.Delay; set => _suggest.Delay = value; }
    public ObservableCollection<MemberHit> Suggestions { get; } = new();
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool ShowSuggestions => HasSuggestions && !IsRegistering;
    partial void OnSearchChanged(string value) { if (!_suppressSearch) _ = SuggestAsync(immediate: false); }
    public Task SuggestNowAsync() => SuggestAsync(immediate: true);

    public static bool Visible(MemberHit hit, string kind) => kind switch
    {
        "Membership" or "Day pass" => !hit.HasActiveMembership,
        "Renewal" => hit.HasActiveMembership && !hit.IsDayPass,
        _ => true,
    };

    static string? IneligibleReason(MemberHit h, string kind)
    {
        if (kind is "Membership" or "Day pass" && h.HasActiveMembership)
            return h.IsDayPass ? $"{h.Username} already has a Day pass until midnight."
                : h.PaidUntil is { } u ? $"{h.Username} already has a membership until {ManilaTime.Of(u):d MMM yyyy}."
                : $"{h.Username} already has an active membership.";
        if (kind == "Renewal" && !h.HasActiveMembership)
            return $"{h.Username} has no active membership to renew.";
        return null;
    }

    public string? OrderIneligibleText => IsWalkIn
        ? (DayPassMember is { HasActiveMembership: true } dm ? IneligibleReason(dm, "Day pass")
            : DayPassWithCoach && sessions is not null && (SelectedDayPassCoach is null || SelectedDayPassHour is null) ? "No coach is free today for a Day pass with coach."
            : null)
        : (Member is { } m ? IneligibleReason(m, Kind) : null);
    public bool HasOrderIneligible => OrderIneligibleText is not null;

    async Task SuggestAsync(bool immediate)
    {
        var (applied, rows, err) = await _suggest.RunAsync(Search, immediate);
        if (!applied) return;
        ErrorMessage = err;
        IsRegistering = false;
        _allHits = rows.ToList();
        ApplyFilter(hasQuery: (Search?.Trim().Length ?? 0) >= 2, err);
    }

    void ApplyFilter(bool hasQuery, string? err)
    {
        var visible = _allHits.Where(h => Visible(h, Kind)).Select(h => h with { IsChosen = Member is not null && h.MemberId == Member.MemberId }).ToList();
        Fill(Suggestions, visible.Take(8));
        Fill(Hits, visible);
        HiddenHint = hasQuery && err is null && _allHits.Count > 0 && visible.Count == 0
            ? IneligibleReason(_allHits[0], Kind) : null;
        NoMatch = hasQuery && err is null && _allHits.Count == 0;
        OnPropertyChanged(nameof(HasSuggestions)); OnPropertyChanged(nameof(ShowSuggestions));
        OnPropertyChanged(nameof(HasHits)); OnPropertyChanged(nameof(ShowHits));
    }

    [RelayCommand]
    private void ChangeCustomer()
    {
        _suggest.Cancel();
        Member = null;
        _suppressSearch = true;
        Search = "";
        _suppressSearch = false;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions)); OnPropertyChanged(nameof(ShowSuggestions));
    }

    [RelayCommand]
    private void ChooseSuggestion(MemberHit hit)
    {
        _suggest.Cancel();
        PickMember(hit);
        _suppressSearch = true;
        Search = hit.Username;
        _suppressSearch = false;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions)); OnPropertyChanged(nameof(ShowSuggestions));
    }

    [RelayCommand]
    private async Task PickTopSuggestionAsync()
    {
        if (Suggestions.Count == 0 && (Search?.Trim().Length ?? 0) >= 2) await SuggestAsync(immediate: true);
        if (Suggestions.Count > 0) ChooseSuggestion(Suggestions[0]);
    }

    /// <summary>The results table shows once a search found someone.</summary>
    public bool HasHits => Hits.Count > 0;
    // owner, 2026-11-16: one at a time: search shows the table, Register new member shows its form
    public bool ShowHits => HasHits && !IsRegistering;
    public bool ShowNoMatch => NoMatch && !IsRegistering;
    partial void OnIsRegisteringChanged(bool value) { OnPropertyChanged(nameof(ShowSuggestions)); OnPropertyChanged(nameof(ShowHits)); OnPropertyChanged(nameof(ShowNoMatch)); OnPropertyChanged(nameof(HasHiddenHint)); }
    partial void OnNoMatchChanged(bool value) => OnPropertyChanged(nameof(ShowNoMatch));
    [RelayCommand]
    private void PickMember(MemberHit hit)
    {
        Member = hit;
        _ = RefreshMemberStatusAsync(hit.MemberId);
    }

    async Task RefreshMemberStatusAsync(string memberId)
    {
        var res = await members.MemberStatusAsync(memberId);
        if (res is not { Ok: true, Value: { } fresh } || Member?.MemberId != memberId) return;
        _refreshingStatus = true;
        Member = fresh;
        _refreshingStatus = false;
    }

    // ---- cashier plan Y2: nobody matched, so the desk registers the customer as a member (search first, so nobody is made twice) ----
    [ObservableProperty] private bool isRegistering;
    [ObservableProperty] private string newFirstName = "";
    [ObservableProperty] private string newLastName = "";
    [ObservableProperty] private string newEmail = "";
    [ObservableProperty] private string newPhone = "";
    [RelayCommand] private void StartRegister() { Notice = null; ErrorMessage = null; IsRegistering = true; }
    [RelayCommand] private void CancelRegister() => IsRegistering = false;

    [RelayCommand]
    private async Task RegisterAsync()
    {
        Notice = null;
        var name = NameParts.Join(NewFirstName.Trim(), NewLastName.Trim());
        if (!await Confirm.AskAsync($"Register {name} as a member?", $"{NewEmail.Trim()}\nThey get a temporary password to change at first sign-in.", "Register member")) return;
        var password = TempPassword.New();
        string? id = null;
        if (!await RunAsync(() => svc.RegisterMemberAsync(NewFirstName, NewLastName, NewEmail, NewPhone, password), v => id = v)) return;
        Search = NewEmail.Trim();
        await RunAsync(() => members.FindMembersAsync(Search), rows => { Fill(Hits, rows); NoMatch = rows.Count == 0; OnPropertyChanged(nameof(HasHits)); OnPropertyChanged(nameof(ShowHits)); Member = rows.FirstOrDefault(h => h.MemberId == id); });
        IsRegistering = false;
        NewFirstName = NewLastName = NewEmail = NewPhone = "";
        Notice = $"{name} is a member now. Temporary password: {password}. Hand it over; they change it at first sign-in.";
    }
    [RelayCommand] private void PickKind(string value) => Kind = value;
    [RelayCommand] private void PickMethod(string value) => Method = value;
    [RelayCommand] private void PickItem(ItemOption option) => Item = option.Item;   // OnItemChanged marks the card

    bool CanComplete() =>
        (IsWalkIn || Member is not null && Item is not null)
        && OrderIneligibleText is null
        && !CashNeedsShift && Waiting is null;

    // The command is off while it runs, so a double tap records one payment.
    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task RecordAsync()
    {
        Notice = null;
        if (!IsWalkIn && (Member is null || Item is null)) { ErrorMessage = "Choose the member and what they are paying for."; return; }
        if (!await Confirm.AskAsync("Record this payment?", $"{OrderWho}\n{OrderWhat}\n{TotalText}\n{OrderHow}"
                + (Kind is "Membership" or "Renewal" ? $"\n\nTell them: {RefundRules.Line}" : ""), "Record payment")) return;   // G23
        _pendingDayPassCoach = DayPassWithCoach && SelectedDayPassCoach is { } dc && SelectedDayPassHour is { } dh
            ? (DayPassMember?.MemberId, DayPassName, dc.CoachId, dh.Hour, Method == Online ? "online" : "cash") : null;
        if (Method == Online) { await StartOnlineAsync(); return; }
        Payment? paid = null;
        if (DayPassMember is { } m)
        {
            if (!await RunAsync(() => svc.RecordCounterAsync(PayKind.Membership, PayMethod.Cash, m.MemberId, dayPassPackage!.Id, null, null, DiscountCode), p => paid = p)) return;
        }
        else if (!await RunAsync(() => svc.RecordCounterAsync(KindKey(Kind), PayMethod.Cash, IsWalkIn ? null : Member!.MemberId, IsWalkIn ? dayPassPackage?.Id : Item?.Id,
                IsWalkIn ? DayPassName : null, null, IsWalkIn ? null : DiscountCode), p => paid = p)) return;
        await DoneAsync(paid!);
    }

    async Task DoneAsync(Payment paid)
    {
        var bookedCoach = false;
        if (_pendingDayPassCoach is { } pcb && sessions is not null)
        {
            _pendingDayPassCoach = null;
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
            var startUtc = new DateTimeOffset(today.ToDateTime(TimeOnly.MinValue).AddHours(pcb.Hour), TimeSpan.FromHours(8)).UtcDateTime;
            if (pcb.MemberId is { } mid)
            {
                var bookRes = await sessions.DeskBookCoachAsync(mid, pcb.CoachId, startUtc, 1, pcb.Method, null);
                bookedCoach = bookRes.Ok;
            }
            else
            {
                var bookRes = await sessions.DeskBookWalkInCoachAsync(pcb.CoachId, startUtc, 1, pcb.WalkInName);
                bookedCoach = bookRes.Ok;
            }
        }
        Notice = $"Payment complete: ₱{paid.Amount:N2}. Receipt {paid.ReceiptNo}." + (bookedCoach ? " Coach session booked." : "");
        Item = null; Member = null; DiscountCode = ""; WalkInName = ""; Waiting = null;   // the next customer starts clean (final review)
        var notice = Notice; await LoadShiftAsync(); Notice = notice;
        RecordCommand.NotifyCanExecuteChanged();
    }

    // cashier plan Y6: a pending online payment, Xendit's checkout on this PC (the customer scans its QR Ph), then Check payment
    async Task StartOnlineAsync()
    {
        Payment? started = null;
        var dayPassFor = DayPassMember;
        if (!await RunAsync(() => dayPassFor is { } m ? svc.StartDeskOnlineAsync(PayKind.Membership, m.MemberId, dayPassPackage!.Id, null, DiscountCode)
                : svc.StartDeskOnlineAsync(KindKey(Kind), IsWalkIn ? null : Member!.MemberId, IsWalkIn ? dayPassPackage?.Id : Item?.Id,
                IsWalkIn ? DayPassName : null, IsWalkIn ? null : DiscountCode), p => started = p)) return;
        Waiting = started; RecordCommand.NotifyCanExecuteChanged();
        await OpenCheckoutAsync();
    }

    [RelayCommand]
    private async Task OpenCheckoutAsync()
    {
        if (Waiting is not { } pay) return;
        OnlineCheckout? checkout = null;
        if (!await RunAsync(() => svc.PayOnlineAsync(pay.Id), c => checkout = c)) return;
        if (checkout!.Paid) { await PaidAsync(pay.Id); return; }
        if (checkout.Url is { } url && opener is not null) await opener.OpenAsync(url);
        Notice = "Xendit's checkout is open on this PC. Let the customer scan the QR Ph code with GCash, Maya or their bank app. This screen updates by itself once they have paid.";
    }

    [RelayCommand]
    private async Task CheckPaymentAsync()
    {
        if (Waiting is not { } pay) return;
        var paid = false;
        if (!await RunAsync(() => svc.CheckOnlineAsync(pay.Id), p => paid = p)) return;
        if (paid) await PaidAsync(pay.Id);
        else Notice = "Not paid yet. Wait for the customer to finish on Xendit's page, then check again.";
    }

    async Task PaidAsync(int paymentId)
    {
        Payment? fresh = null;
        if (await RunAsync(() => svc.PaymentAsync(paymentId), p => fresh = p)) await DoneAsync(fresh!);
    }

    /// <summary>The customer walked away: the pending payment is cancelled, so the member can pay another way at once (final review).
    /// A late scan on Xendit's still-open page goes to staff as "Online payment to refund".</summary>
    [RelayCommand]
    private async Task StopWaitingAsync()
    {
        if (Waiting is not { } pay) return;
        if (!await RunAsync(() => svc.VerifyAsync(pay.Id, false, "Customer left at the desk"))) return;
        Waiting = null; Notice = "Cancelled. Nothing was charged."; RecordCommand.NotifyCanExecuteChanged();
    }
}

public partial class CheckInViewModel(IAttendanceService svc, ICoachService coaches, OfflineDesk? offline = null, IProfileService? profile = null, TimeProvider? clock = null, IReportFiles? files = null, ISubscriptionService? refunds = null, ISessionService? sessions = null) : LoadableViewModel
{
    // ---- owner, 2026-11-18: View on a Day pass in Checked in today shows who and what was paid; Print opens the receipt PDF ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsViewingDayPass))] private CheckInRow? viewingDayPass;
    public bool IsViewingDayPass => ViewingDayPass is not null;
    [RelayCommand] private void ViewDayPass(CheckInRow row) { ViewingDayPass = row; IsAskingRefund = false; }
    [RelayCommand] private void CloseDayPass() { ViewingDayPass = null; IsAskingRefund = false; }

    // ---- 0096: the desk asks for the refund with a reason; the System admin decides on Refunds ----
    [ObservableProperty] private bool isAskingRefund;
    [ObservableProperty] private string refundReason = "";
    [RelayCommand] private void AskRefund() { RefundReason = ""; IsAskingRefund = true; Notice = null; }
    [RelayCommand] private void KeepRefund() => IsAskingRefund = false;

    [RelayCommand]
    private async Task SendRefundAsync()
    {
        if (refunds is null || ViewingDayPass is not { DayPass: { CanRequestRefund: true } pass }) return;
        if (string.IsNullOrWhiteSpace(RefundReason)) { ErrorMessage = "Say why the customer wants a refund."; return; }
        Notice = null;
        if (!await RunAsync(() => refunds.RequestWalkInRefundAsync(pass.PaymentId, RefundReason))) return;
        IsAskingRefund = false; Notice = "Refund requested. The System admin decides it on Refunds.";
        await LoadTodayAsync();
    }

    [RelayCommand]
    private async Task PrintDayPassAsync()
    {
        if (files is null || ViewingDayPass is not { DayPass: { CanPrint: true } pass } row) return;
        Notice = null;
        if (await RunAsync(() => Safe.RunAsync(async () => { await files.OpenAsync($"receipt-{pass.ReceiptNo}.pdf", DayPassReceiptPdf.Build(row, pass, DateTime.UtcNow, await files.FontAsync())); return true; })))
            Notice = "Receipt opened. Print it from the PDF viewer (Ctrl+P).";
    }

    public ObservableCollection<MemberHit> Hits { get; } = new();
    public ObservableCollection<CoachPresenceRow> Coaches { get; } = new();
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string scanInput = "";   // a QR scanner device types the code here and presses Enter
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasScan))] private MemberScan? scan;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasScannedSession))] private TodayCoachSession? scannedSession;
    public ObservableCollection<TodayCoachSession> MemberTodaySessions { get; } = new();
    [ObservableProperty] private bool hasMemberTodaySessions;
    [ObservableProperty] private bool isScanning;          // the camera is on
    public bool HasScan => Scan is not null;
    public bool HasScannedSession => ScannedSession is not null;

    // D12: when the connection drops, the desk works from its saved list (OfflineDesk)
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OfflineText), nameof(CanUsePassword))] private bool isOffline;
    public string OfflineText => offline?.OfflineText ?? "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasQueue))] private string queueText = "";
    public bool HasQueue => QueueText.Length > 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRefused))] private string refusedText = "";
    public bool HasRefused => RefusedText.Length > 0;

    bool WentOffline<T>(AppResult<T> result) => offline is not null && result.Error == AppErrorKind.Offline;

    void ShowQueue()
    {
        if (offline is null) return;
        QueueText = offline.Queued.Count switch { 0 => "", 1 => "1 check-in is waiting to send.", var n => $"{n} check-ins are waiting to send." };
        RefusedText = string.Join("\n", offline.Refused.Select(r => $"{r.Name}, {RevenuePdf.Manila(r.AtUtc)} (Manila), was refused when sent: {r.Why}"));
        OnPropertyChanged(nameof(OfflineText));
    }

    void QueueFor(string memberId, string name, string? pin, string? reason, string? idType = null)
    {
        offline!.Queue(memberId, name, pin, reason, idType);
        IsOffline = true; ErrorMessage = null;
        Notice = $"{name} is saved and will be sent when the connection is back.";
        ShowQueue();
    }

    public override Task OnAppearingAsync() => Task.WhenAll(
        LoadCanSkipPinAsync(),
        SearchAsync(),
        LoadCoachesAsync(),
        SendQueueAsync(),
        LoadTodayAsync());

    async Task LoadCanSkipPinAsync()
    {
        if ((await svc.CanSkipPinAsync()) is { Ok: true } skip) CanSkipPin = skip.Value;   // quiet: offline it stays as it was
    }

    // ---- F19 (Phase 7): who came in today, newest first, with the count and a name filter; reloads after each check-in ----
    public ObservableCollection<CheckInRow> Today { get; } = new();
    IReadOnlyList<CheckInRow> _today = [];
    [ObservableProperty] private string todayFilter = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TodayTitle))] private int todayCount;
    public string TodayTitle => $"Checked in today: {TodayCount}";
    /// <summary>"2 of 42 match" while a filter is typed; blank otherwise.</summary>
    public string TodayMatchText => TodayFilter.Trim().Length == 0 ? "" : $"{Today.Count} of {TodayCount} match";
    public bool IsTodayEmpty => TodayCount == 0;

    partial void OnTodayFilterChanged(string value) => ShowToday();

    void ShowToday()
    {
        var f = TodayFilter.Trim();
        Sync(Today, f.Length == 0 ? _today : _today.Where(r => r.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList(), r => (r.Name, r.At), (a, b) => a == b);
        OnPropertyChanged(nameof(TodayMatchText));
        OnPropertyChanged(nameof(IsTodayEmpty));
    }

    /// <summary>Quiet: a failure (or offline) leaves the list as it was; the page's own error line covers the rest.</summary>
    async Task LoadTodayAsync()
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var manila = DateOnly.FromDateTime(now.AddHours(8));   // Manila has no daylight saving
        if (await svc.CheckInsOnAsync(manila) is not { Ok: true, Value: { } rows }) return;
        // tinted for a minute: the live hint and the desk's own reload can come one after the other, so "new" means recent
        _today = rows.OrderByDescending(r => r.At).Select(r => r with { IsNew = now - r.At < TimeSpan.FromMinutes(1) }).ToList();
        TodayCount = _today.Count;
        ShowToday();
        if (ViewingDayPass is { } open) ViewingDayPass = _today.FirstOrDefault(r => r.At == open.At && r.Name == open.Name) ?? open;   // the open panel shows the refund as it stands
    }

    /// <summary>Sends anything queued and keeps the saved list up to date. Quiet when offline: the next tick tries again.</summary>
    [RelayCommand]
    private async Task SendQueueAsync()
    {
        if (offline is null) return;
        var (sent, refused) = await offline.SendAsync();
        if (await offline.RefreshAsync(force: sent + refused > 0) is AppErrorKind.Offline) IsOffline = true;
        if (sent > 0) Notice = sent == 1 ? "1 offline check-in was sent." : $"{sent} offline check-ins were sent.";
        ShowQueue();
    }

    [RelayCommand] private void ClearRefused() { offline?.ClearRefused(); ShowQueue(); }

    [RelayCommand] private void ToggleCamera() => IsScanning = !IsScanning;

    /// <summary>D12: shows who the code belongs to and their live status. Never checks in by itself. F6: a pass can't be checked offline.</summary>
    [RelayCommand]
    private async Task ScanAsync(string? code)
    {
        code = (code ?? ScanInput).Trim();
        if (code.Length == 0) return;
        Notice = null; IsScanning = false; ScanInput = "";
        if (sessions is not null && code.StartsWith("SES-", StringComparison.OrdinalIgnoreCase))
        {
            Scan = null; MemberTodaySessions.Clear(); HasMemberTodaySessions = false;
            if (await RunAsync(() => sessions.ScanSessionAsync(code), s => ScannedSession = s))
                Notice = $"{ScannedSession!.MemberName} checked in for {ScannedSession.TimeRange}.";
            return;
        }
        ScannedSession = null;
        MemberTodaySessions.Clear(); HasMemberTodaySessions = false;
        var result = await svc.ScanAsync(code);
        if (WentOffline(result)) { IsOffline = true; ErrorMessage = null; Scan = new MemberScan { Result = "saved_no_qr" }; ShowQueue(); return; }
        await RunAsync(() => Task.FromResult(result), s => { Scan = s; IsOffline = false; });
        // G10: the card shows the face, or the initials while the photo loads or when there is none
        (ScanPhoto, ScanLetters) = (null, Scan?.Name is { } n ? Initials.From(n) : "");
        if (profile is not null && Scan is { AvatarPath: { } path } shown && (await profile.AvatarUrlAsync(path)) is { Ok: true, Value: { } url } && Scan == shown) ScanPhoto = url;
        if (sessions is not null && Scan?.Member is { } mid) await LoadMemberTodaySessionsAsync(mid);
    }

    async Task LoadMemberTodaySessionsAsync(string memberId)
    {
        if (sessions is null) return;
        var res = await sessions.TodayCoachSessionsAsync();
        if (!res.Ok || res.Value is null) return;
        Fill(MemberTodaySessions, res.Value.Where(s => s.MemberId == memberId).ToList());
        HasMemberTodaySessions = MemberTodaySessions.Count > 0;
    }

    [RelayCommand]
    private async Task CheckInSessionAsync(TodayCoachSession row)
    {
        if (sessions is null || string.IsNullOrWhiteSpace(row.SessionCode)) return;
        Notice = null;
        if (!await RunAsync(() => sessions.ScanSessionAsync(row.SessionCode), s => { if (ScannedSession?.SessionId == s.SessionId) ScannedSession = s; })) return;
        Notice = $"{row.MemberName} checked in for {row.TimeRange}.";
        if (Scan?.Member is { } mid) await LoadMemberTodaySessionsAsync(mid);
    }

    [RelayCommand]
    private async Task MarkCoachArrivedAsync(TodayCoachSession row)
    {
        if (sessions is null) return;
        Notice = null;
        if (!await RunAsync(() => sessions.MarkCoachArrivedAsync(row.SessionId))) return;
        Notice = $"{row.CoachName} marked arrived. Session is now In session.";
        if (ScannedSession?.SessionId == row.SessionId)
            ScannedSession = ScannedSession with { CoachArrivedAt = DateTime.UtcNow, Status = "In session" };
        if (Scan?.Member is { } mid) await LoadMemberTodaySessionsAsync(mid);
    }

    [RelayCommand] private void ClearScannedSession() => ScannedSession = null;
    [ObservableProperty] private string? scanPhoto;
    [ObservableProperty] private string scanLetters = "";

    [RelayCommand]
    private async Task CheckInScannedAsync()
    {
        if (Scan is not { CanCheckIn: true, Member: { } id } s) return;
        Notice = null;
        var result = await svc.CheckInAsync(id, "qr");
        if (WentOffline(result))
        {   // the pass is used up and can't be checked offline (F6), so the name and PIN are the way in
            IsOffline = true; Scan = null;
            ErrorMessage = $"The connection dropped. Find {s.Name} by name and check them in with their desk PIN.";
            return;
        }
        if (await RunAsync(() => Task.FromResult(result))) { Notice = $"{s.Name} is checked in."; Scan = null; await LoadTodayAsync(); }
    }

    [RelayCommand] private void ClearScan() => Scan = null;

    [RelayCommand]
    private async Task SearchAsync()
    {
        var result = await svc.FindMembersAsync(Search);
        if (WentOffline(result)) { IsOffline = true; ErrorMessage = null; Fill(Hits, Listed(offline!.Search(Search))); ShowQueue(); return; }
        await RunAsync(() => Task.FromResult(result), rows => { Fill(Hits, Listed(rows)); IsOffline = false; });
    }

    /// <summary>Hides deactivated accounts and the member whose PIN step is open (so they aren't listed twice).</summary>
    IEnumerable<MemberHit> Listed(IEnumerable<MemberHit> rows) => rows.Where(h => h.Status != "inactive" && h.MemberId != PinFor?.MemberId);

    async Task LoadCoachesAsync()
    {
        var result = await coaches.PresenceAsync();
        if (WentOffline(result)) return;   // the list on screen stays; coaches can't change without a connection anyway
        await RunAsync(() => Task.FromResult(result), rows => Fill(Coaches, rows));
    }

    // ---- F8: check-in by name asks for the member's desk PIN first; only Admin and System admin may give a reason instead ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPinStep))] private MemberHit? pinFor;
    public bool HasPinStep => PinFor is not null;
    [ObservableProperty] private string pin = "";
    [ObservableProperty] private string reason = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowsReason))] private bool canSkipPin;
    /// <summary>F8: the member's photo, so staff can compare the face; the initials when there is none.</summary>
    [ObservableProperty] private string? pinPhoto;
    [ObservableProperty] private string pinLetters = "";

    /// <summary>Forgot phone: staff find the member by name and see their status; Check in opens the PIN step. The server still decides.</summary>
    [RelayCommand]
    private async Task CheckInAsync(MemberHit hit)
    {
        Notice = null; ErrorMessage = null;
        if (!hit.CanCheckIn)
        {
            ErrorMessage = IsOffline ? $"The saved list shows no paid term for {hit.Username}." : $"{hit.Username} has no active membership.";
            return;
        }
        ClosePin();   // another member's step, if one was open, gives its row back
        _pinRow = Math.Max(0, Hits.IndexOf(hit));
        Hits.Remove(hit);
        (PinFor, Pin, Reason, PinPhoto, PinLetters) = (hit, "", "", null, Initials.From(hit.Username));
        if (profile is not null && hit.AvatarPath is not null && !IsOffline
            && (await profile.AvatarUrlAsync(hit.AvatarPath)) is { Ok: true, Value: { } url } && PinFor == hit) PinPhoto = url;
    }
    int _pinRow;

    void ClosePin()
    {
        if (PinFor is { } hit && Hits.All(h => h.MemberId != hit.MemberId)) Hits.Insert(Math.Min(_pinRow, Hits.Count), hit);
        (PinFor, Pin, Reason, PinPhoto, Password, IsPasswordMode) = (null, "", "", null, "", false);
        (IsIdMode, IdType, IdMatches) = (false, null, false);
    }

    // ---- G12, login mode: no phone and no PIN, so the member types their own account password; online only, never kept ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPinMode), nameof(ShowsReason))] private bool isPasswordMode;
    [ObservableProperty] private string password = "";
    public bool CanUsePassword => !IsOffline;

    [RelayCommand] private void UsePassword() { (IsPasswordMode, Pin, ErrorMessage) = (true, "", null); (IsIdMode, IdType, IdMatches) = (false, null, false); }
    [RelayCommand] private void UsePin() { (IsPasswordMode, Password, ErrorMessage) = (false, "", null); (IsIdMode, IdType, IdMatches) = (false, null, false); }

    // ---- G20/G21 (0066): the desk looks at the member's ID and compares it with the photo on the card; only the ID's type is kept ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPinMode), nameof(ShowsReason))] private bool isIdMode;
    [ObservableProperty] private string? idType;
    [ObservableProperty] private bool idMatches;
    /// <summary>One chip per ID type; the picked one is IdType.</summary>
    public IReadOnlyList<PickChip> IdChips { get; } = Services.IdTypes.Labels.Select(l => new PickChip(l, true)).ToList();
    partial void OnIdTypeChanged(string? value) { foreach (var c in IdChips) c.IsPicked = c.Text == value; }
    [RelayCommand] private void PickIdType(PickChip chip) { (IdType, ErrorMessage) = (chip.Text, null); }
    /// <summary>The desk PIN step shows unless the password or the ID step is open; an Admin's reason goes only with it.</summary>
    public bool IsPinMode => !IsPasswordMode && !IsIdMode;
    public bool ShowsReason => CanSkipPin && IsPinMode;

    [RelayCommand] private void UseId() { (IsIdMode, IsPasswordMode, Pin, Password, ErrorMessage) = (true, false, "", "", null); }

    [RelayCommand]
    private async Task ConfirmIdAsync()
    {
        if (PinFor is not { } hit) return;
        Notice = null;
        if (Services.IdTypes.Code(IdType) is not { } type) { ErrorMessage = "Pick the type of ID the member showed."; return; }
        if (!IdMatches) { ErrorMessage = "Tick that the name and photo match this member."; return; }
        if (IsOffline) { QueueFor(hit.MemberId, hit.Username, null, null, type); ClosePin(); return; }
        var result = await svc.CheckInAsync(hit.MemberId, "id", idType: type);
        if (WentOffline(result)) { QueueFor(hit.MemberId, hit.Username, null, null, type); ClosePin(); return; }
        if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        ClosePin(); ErrorMessage = null;
        Notice = $"{hit.Username} is checked in.";
        await LoadTodayAsync();
    }

    [RelayCommand]
    private async Task ConfirmPasswordAsync()
    {
        if (PinFor is not { } hit) return;
        Notice = null;
        var typed = Password;
        Password = "";   // cleared after every try, right or wrong
        if (typed.Length == 0) { ErrorMessage = "Ask the member to type their account password."; return; }
        var proof = await svc.VerifyPasswordAsync(hit.MemberId, typed);
        if (!proof.Ok) { ErrorMessage = ErrorText.For(proof.Error, proof.Message); return; }
        if (!await RunAsync(() => svc.CheckInAsync(hit.MemberId, "password", proof: proof.Value))) return;
        ClosePin(); ErrorMessage = null;
        Notice = $"{hit.Username} is checked in.";
        await LoadTodayAsync();
    }

    [RelayCommand] private void CancelPin() { ClosePin(); ErrorMessage = null; }

    [RelayCommand]
    private async Task ConfirmPinAsync()
    {
        if (PinFor is not { } hit) return;
        Notice = null;
        var (pin, reason) = (Pin.Trim(), Reason.Trim());
        if (pin.Length == 0 && !(CanSkipPin && reason.Length > 0))
        {
            ErrorMessage = CanSkipPin ? "Enter the member's desk PIN, or a reason for checking them in without it." : "Enter the member's desk PIN.";
            return;
        }
        string? p = pin.Length > 0 ? pin : null, r = p is null ? reason : null;
        if (IsOffline) { QueueFor(hit.MemberId, hit.Username, p, r); ClosePin(); return; }
        var result = await svc.CheckInAsync(hit.MemberId, "manual", p, r);
        if (WentOffline(result)) { QueueFor(hit.MemberId, hit.Username, p, r); ClosePin(); return; }
        Pin = "";
        if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        ClosePin(); ErrorMessage = null;
        Notice = $"{hit.Username} is checked in.";
        await LoadTodayAsync();
    }

    [RelayCommand]
    private async Task ToggleCoachAsync(CoachPresenceRow row)
    {
        Notice = null;
        if (!await (row.InGym ? Confirm.AskAsync($"Check {row.Name} out?", "Members will no longer see them as in the gym.", "Check out")
                              : Confirm.AskAsync($"Check {row.Name} in?", "Members will see they are here.", "Check in"))) return;
        var ok = await RunAsync(() => row.InGym ? coaches.CheckOutAsync(row.CoachId) : coaches.CheckInAsync(row.CoachId));
        if (!ok) return;
        Notice = row.InGym ? $"{row.Name} is checked out." : $"{row.Name} is checked in.";
        await LoadCoachesAsync();
    }
}

// ---------- coach ----------
public partial class CoachProfileViewModel(ICoachService svc) : LoadableViewModel
{
    [ObservableProperty] private string? bio;
    [ObservableProperty] private string? specialty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HiringText))] private bool isAvailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InGym), nameof(VisitText), nameof(VisitButton))] private CoachVisit? visit;

    public bool InGym => Visit is not null;
    public string VisitText => Visit is { } v ? $"You are checked in since {Fmt.Time(v.CheckedInAt)}. Members can see you are here." : "You are not checked in. Members won't see you in the coach list until you are.";
    public string VisitButton => InGym ? "Check out" : "Check in";
    public string HiringText => IsAvailable ? "On" : "Off";

    public override async Task OnAppearingAsync()
    {
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var coachTask = RunAsync(svc.MyCoachAsync, c =>
        { if (c is null) return; Bio = c.Bio; Specialty = c.Specialty; IsAvailable = c.IsAvailable; });
        var visitTask = RunAsync(svc.MyVisitAsync, v => Visit = v);
        await Task.WhenAll(coachTask, visitTask);
    }

    [RelayCommand]
    private async Task ToggleVisitAsync()
    {
        Notice = null;
        var wasIn = InGym;
        if (!await (wasIn ? Confirm.AskAsync("Check out of the gym?", "Members will no longer see you as in the gym.", "Check out")
                          : Confirm.AskAsync("Check in to the gym?", "Members will see you are here.", "Check in"))) return;
        if (!await RunAsync(() => wasIn ? svc.CheckOutAsync() : svc.CheckInAsync())) return;
        await RunAsync(svc.MyVisitAsync, v => Visit = v);
        Notice = wasIn ? "You are checked out." : "You are checked in.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Notice = null;
        if (await RunAsync(() => svc.UpdateProfileAsync(Bio, Specialty, IsAvailable))) Notice = "Profile saved.";
    }
}

public partial class TraineesViewModel(ICoachService svc, IChatService messages, IAppNavigator nav) : LoadableViewModel
{
    public ObservableCollection<TraineeRow> Trainees { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.TraineesAsync, rows => Fill(Trainees, rows));

    [RelayCommand]
    private void Message(TraineeRow row) { messages.Pending = (row.CoachId, row.MemberId); nav.GoToMessages(); }
}

public partial class ShiftsViewModel(ICoachService svc, ISessionService? sessions = null, IAttendanceService? attendance = null, IMembershipService? gym = null) : LoadableViewModel
{
    public AttendanceViewModel? Calendar { get; } = sessions is not null && attendance is not null
        ? new AttendanceViewModel(sessions, attendance, svc, gym)
        : null;
    public ObservableCollection<ShiftPost> Shifts { get; } = new();
    public int[] HoursOptions { get; } = [2, 4, 6, 8, 10];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PostTimeHint), nameof(HasPostTimeHint)), NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PostTimeHint), nameof(HasPostTimeHint)), NotifyCanExecuteChangedFor(nameof(PostCommand))]
    private TimeSpan time = new(8, 0, 0);
    [ObservableProperty] private int hours = 4;   // D10 default length
    [ObservableProperty] private string? note;

    public string? PostTimeHint => (Date.Date + Time) <= DateTime.Now ? "Pick a future start time — this time has already passed." : null;
    public bool HasPostTimeHint => PostTimeHint is not null;
    bool CanPost() => !HasPostTimeHint;

    // hiring on/off lives on My coach profile ("Taking new members"); 9.6 took the duplicate Pause hiring button off this page
    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => Calendar is not null
        ? Task.WhenAll(RunAsync(svc.MyShiftsAsync, rows => Fill(Shifts, rows)), Calendar.OnAppearingAsync())
        : RunAsync(svc.MyShiftsAsync, rows => Fill(Shifts, rows));

    [RelayCommand(CanExecute = nameof(CanPost))]
    private async Task PostAsync()
    {
        Notice = null;
        var start = (Date.Date + Time).ToUniversalTime();
        if (await RunAsync(() => svc.PostShiftAsync(start, start.AddHours(Hours), Note)))
        { Notice = "Shift posted. Your trainees can see it on the Coaches page."; Note = null; await LoadAsync(); }
    }

    [RelayCommand]
    private async Task CancelAsync(ShiftPost shift)
    {
        if (!await Confirm.AskAsync("Cancel this shift?", Fmt.Range(shift.Start, shift.End), "Cancel shift")) return;
        if (await RunAsync(() => svc.CancelShiftAsync(shift.Id))) await LoadAsync();
    }
}

public partial class RequestsViewModel(ISessionService svc) : LoadableViewModel
{
    public ObservableCollection<RequestRow> Requests { get; } = new();
    public ObservableCollection<CoachCard> Coaches { get; } = new();
    public ObservableCollection<RequestMember> Members { get; } = new();
    public ObservableCollection<AmenityChoice> AmenityChoices { get; } = new();   // "No amenity" first
    public int[] Durations { get; } = [30, 45, 60, 90];
    [ObservableProperty] private AmenityChoice? selectedAmenity;

    [ObservableProperty] private bool isCoach;
    [ObservableProperty] private bool canCompose;
    [ObservableProperty] private bool useWindowsRequestForm;
    [ObservableProperty] private CoachCard? selectedCoach;
    [ObservableProperty] private RequestMember? selectedMember;
    [ObservableProperty] private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan time = new(9, 0, 0);
    [ObservableProperty] private int durationMinutes = 60;
    [ObservableProperty] private string? message;
    [ObservableProperty] private string waitingText = "–";
    [ObservableProperty] private string nextSessionText = "–";

    IReadOnlyList<TrainingSession> activeSessions = [];
    public bool HasActiveSession => IsCoach
        ? SelectedMember is { } member && activeSessions.Any(s => s.MemberId == member.MemberId)
        : SelectedCoach is { } coach && activeSessions.Any(s => s.CoachId == coach.Coach.UserId);
    public bool ShowActiveSession => UseWindowsRequestForm && HasActiveSession;
    public bool ShowMemberPicker => UseWindowsRequestForm && IsCoach;
    public bool HasPendingRequest => !IsCoach && SelectedCoach is { } c && Requests.Any(r => r.CanCancel && r.Request.RequestedBy == "member" && r.Request.SessionId is null && r.Request.CoachId == c.Coach.UserId);
    public bool ShowPendingRequest => UseWindowsRequestForm && HasPendingRequest && !HasActiveSession;
    public string ComposeTitle => IsCoach ? (UseWindowsRequestForm ? "Invite a member" : "Offer an open slot") : "Request a session";
    public string SubmitText => IsCoach ? (UseWindowsRequestForm ? "Send invitation" : "Post slot") : "Send request";
    public string MemberPickerHint => Members.Count == 0 ? "No active trainees to invite. A member must hire you first." : "Choose one of your current trainees. Only that member receives this invitation.";
    public bool CanSubmit => !IsBusy && CanCompose && !HasActiveSession && (IsCoach ? !UseWindowsRequestForm || SelectedMember is not null : SelectedCoach is not null && !HasPendingRequest);

    partial void OnSelectedCoachChanged(CoachCard? value) => RefreshCompose();
    partial void OnSelectedMemberChanged(RequestMember? value) => RefreshCompose();
    partial void OnUseWindowsRequestFormChanged(bool value) => RefreshCompose();
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) SubmitCommand.NotifyCanExecuteChanged();
    }
    void RefreshCompose()
    {
        foreach (var name in new[] { nameof(HasActiveSession), nameof(ShowActiveSession), nameof(ShowMemberPicker), nameof(HasPendingRequest), nameof(ShowPendingRequest), nameof(ComposeTitle), nameof(SubmitText), nameof(MemberPickerHint) }) OnPropertyChanged(name);
        SubmitCommand.NotifyCanExecuteChanged();
    }

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        CanCompose = false;
        await RunAsync(svc.RequestsAsync, v =>
        {
            activeSessions = v.ActiveSessions ?? [];
            Fill(Requests, v.Rows); Fill(Coaches, v.HiredCoaches); Fill(Members, v.Trainees ?? []);
            WaitingText = v.Rows.Count.ToString();
            NextSessionText = activeSessions.MinBy(s => s.ScheduledStart) is { } next
                ? next.ScheduledStart.ToLocalTime().ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture) : "None booked";
            Fill(AmenityChoices, new[] { new AmenityChoice(null, "No amenity") }.Concat((v.Amenities ?? []).Select(a => new AmenityChoice(a.Id, a.Name))));
            SelectedAmenity = AmenityChoices.FirstOrDefault(a => a.Id == SelectedAmenity?.Id) ?? AmenityChoices.First();
            IsCoach = v.IsCoach;
            CanCompose = v.IsCoach || v.HiredCoaches.Count > 0;
            SelectedCoach = Coaches.FirstOrDefault(c => c.Coach.UserId == SelectedCoach?.Coach.UserId) ?? Coaches.FirstOrDefault();
            SelectedMember = Members.FirstOrDefault(m => m.MemberId == SelectedMember?.MemberId);
        });
        RefreshCompose();
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (!CanSubmit) return;
        Notice = null;
        var start = (Date.Date + Time).ToUniversalTime();
        var end = start.AddMinutes(DurationMinutes);
        var (title, button) = !IsCoach ? ($"Send this request to {SelectedCoach?.Name}?", "Send request")
            : UseWindowsRequestForm ? ($"Invite {SelectedMember?.Name}?", "Send invitation") : ("Post this open time?", "Post");
        if (!await Confirm.AskAsync(title, Fmt.Range(start, end), button)) return;
        var ok = IsCoach
            ? UseWindowsRequestForm
                ? await RunAsync(() => svc.InviteMemberAsync(SelectedMember!.MemberId, start, end))
                : await RunAsync(() => svc.PostAvailabilityAsync(start, end))
            : await RunAsync(() => svc.RequestAsync(SelectedCoach!.Coach.UserId, start, end, Message, SelectedAmenity?.Id));
        if (ok) { Notice = IsCoach ? (UseWindowsRequestForm ? "Invitation sent. The member must approve it before a session is booked." : "Availability posted.") : "Request sent."; Message = null; await LoadAsync(); }
        else if (ErrorMessage == ErrorText.For(AppErrorKind.Conflict, "pending_session_request") ||
                 ErrorMessage == ErrorText.For(AppErrorKind.Conflict, "active_coaching_session"))
        {
            var error = ErrorMessage;
            await LoadAsync();
            ErrorMessage = error;
        }
    }

    // ---------- answering a request (D8): one panel for propose, ask for another time and decline ----------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResponseTitle), nameof(ResponseNeedsTime), nameof(ResponseSubmitText), nameof(ResponseReasonHint))]
    private string responseMode = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsResponding), nameof(ResponseTitle))] private RequestRow? responding;
    [ObservableProperty] private DateTime responseDate = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan responseTime = new(9, 0, 0);
    [ObservableProperty] private int responseDuration = 60;
    [ObservableProperty] private AmenityChoice? responseAmenity;
    [ObservableProperty] private string? responseReason;
    public bool IsResponding => Responding is not null;
    public bool ResponseNeedsTime => ResponseMode != "decline";
    public string ResponseTitle => ResponseMode switch
    {
        "propose" => $"Propose another time to {Responding?.With}", "another" => "Ask your coach for another time", _ => $"Decline the request from {Responding?.With}"
    };
    public string ResponseSubmitText => ResponseMode switch { "propose" => "Send proposal", "another" => "Send request", _ => "Decline" };
    public string ResponseReasonHint => ResponseMode switch
    {
        "propose" => "Why this time? The member sees it.", "another" => "Optional note for your coach", _ => "Reason (the other person sees it)"
    };

    void StartResponse(RequestRow row, string mode)
    {
        Notice = null; ErrorMessage = null;
        var start = (row.Request.ProposedStart ?? row.Request.RequestedStart).ToLocalTime();
        var end = (row.Request.ProposedEnd ?? row.Request.RequestedEnd).ToLocalTime();
        ResponseDate = start.Date; ResponseTime = start.TimeOfDay; ResponseDuration = (int)Math.Max(30, (end - start).TotalMinutes);
        ResponseAmenity = AmenityChoices.FirstOrDefault(a => a.Id == (row.Request.ProposedAmenityId ?? row.Request.AmenityId)) ?? AmenityChoices.FirstOrDefault();
        ResponseReason = null; ResponseMode = mode; Responding = row;
    }

    [RelayCommand] private void StartPropose(RequestRow row) => StartResponse(row, "propose");
    [RelayCommand] private void StartAnother(RequestRow row) => StartResponse(row, "another");
    [RelayCommand] private void StartDecline(RequestRow row) => StartResponse(row, "decline");
    [RelayCommand] private void CloseResponse() => Responding = null;

    [RelayCommand]
    private async Task SubmitResponseAsync()
    {
        if (Responding is not { } row) return;
        var start = (ResponseDate.Date + ResponseTime).ToUniversalTime();
        var end = start.AddMinutes(ResponseDuration);
        var id = row.Request.Id;
        var (title, button) = ResponseMode switch
        {
            "propose" => ("Send this time instead?", "Send"),
            "another" => ("Ask for this time instead?", "Send"),
            _ => ("Decline this request?", "Decline")
        };
        if (!await Confirm.AskAsync(title, ResponseMode is "propose" or "another" ? $"With {row.With}, {Fmt.Range(start, end)}." : $"With {row.With}.", button)) return;
        var ok = ResponseMode switch
        {
            "propose" => await RunAsync(() => svc.ProposeAsync(id, start, end, ResponseAmenity?.Id, ResponseReason ?? "")),
            "another" => await RunAsync(() => svc.RequestAnotherTimeAsync(id, start, end, ResponseAmenity?.Id, ResponseReason)),
            _ => await RunAsync(() => svc.DeclineAsync(id, ResponseReason ?? ""))
        };
        if (!ok) return;   // the panel stays open with what was typed and the reason it failed
        Notice = ResponseMode switch
        {
            "propose" => "Proposal sent. The member has 24 hours, or until it starts, to accept.",
            "another" => "Sent. Your coach will answer the new time.", _ => "Declined."
        };
        Responding = null;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AcceptAsync(RequestRow row)
    {
        Notice = null;
        if (!await Confirm.AskAsync("Accept this time?", $"{row.ProposalText ?? $"With {row.With}"}\nThe session is booked.", "Accept")) return;
        var result = "";
        if (!await RunAsync(() => svc.AcceptProposalAsync(row.Request.Id, row.Request.Version), r => result = r)) { var error = ErrorMessage; await LoadAsync(); ErrorMessage = error; return; }
        await LoadAsync();
        Notice = result == "expired" ? "That proposal expired before you accepted it. Your coach will answer again." : "Booked. The session is on your calendar.";
    }

    [RelayCommand] private Task ApproveAsync(RequestRow row) => RespondAsync(row, true);
    [RelayCommand] private Task RejectAsync(RequestRow row) => RespondAsync(row, false);

    [RelayCommand]
    private async Task CancelAsync(RequestRow row)
    {
        if (!await Confirm.AskAsync("Cancel this request?", $"With {row.With}.", "Cancel request")) return;
        if (await RunAsync(() => svc.CancelRequestAsync(row.Request.Id))) await LoadAsync();
    }

    async Task RespondAsync(RequestRow row, bool approve)
    {
        Notice = null;
        if (!await (approve ? Confirm.AskAsync("Approve this request?", $"With {row.With}. The session goes on the calendar.", "Approve")
                            : Confirm.AskAsync("Decline this request?", $"With {row.With}.", "Decline"))) return;
        if (await RunAsync(() => svc.RespondAsync(row.Request.Id, approve)))
        { Notice = approve ? "Approved. The session is on the calendar." : "Rejected."; await LoadAsync(); }
    }
}

public partial class SessionsViewModel(ISessionService svc, IMembershipService? membership = null, IUrlOpener? opener = null) : LoadableViewModel
{
    /// <summary>The rows the status filter lets through, scheduled first (soonest first), then the rest newest first; the full list stays in <c>all</c>.</summary>
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    IReadOnlyList<SessionRow> all = [];
    /// <summary>All, Scheduled, Completed or Cancelled (which also covers no-shows). Kept across reloads.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EmptyText))] private string filter = "All";
    public string EmptyText => Filter == "All" ? "No sessions yet. Book a coach above." : "No sessions with this status. Choose All to see every session.";
    [ObservableProperty] private SessionRow? rescheduling;
    [ObservableProperty] private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan time = new(9, 0, 0);

    public override async Task OnAppearingAsync()
    {
        var pending = svc.PendingCoachId;
        if (pending is not null)
        {
            svc.PendingCoachId = null;
            IsBookingCoach = true;
            await Task.WhenAll(LoadAsync(), LoadPackageCoachInfoAsync(), LoadFreeCoachesAsync(pending));
            return;
        }
        await Task.WhenAll(LoadAsync(), LoadPackageCoachInfoAsync());
    }

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.SessionsAsync, rows => { all = Ordered(rows); Apply(); OnPropertyChanged(nameof(HasMemberActiveSession)); });
    [RelayCommand] private void SetFilter(string f) => Filter = f;
    partial void OnFilterChanged(string value) => Apply();
    public bool HasMemberActiveSession => CanShowBookCoach && all.Any(r => r.Session.Status == Status.Scheduled && (r.Session.HeldUntil is null || r.Session.HeldUntil > DateTime.UtcNow));

    void Apply() => Fill(Sessions, all.Where(r => Filter switch
    {
        "Scheduled" => r.Session.Status == Status.Scheduled,
        "Completed" => r.Session.Status == Status.Completed,
        "Cancelled" => r.Session.Status is Status.Cancelled or Status.NoShow,
        _ => true,
    }));

    /// <summary>What still needs doing first: scheduled sessions soonest first, then history newest first.</summary>
    public static IReadOnlyList<SessionRow> Ordered(IEnumerable<SessionRow> rows) =>
    [
        .. rows.Where(r => r.Session.Status == Status.Scheduled).OrderBy(r => r.Session.ScheduledStart),
        .. rows.Where(r => r.Session.Status != Status.Scheduled).OrderByDescending(r => r.Session.ScheduledStart),
    ];

    [RelayCommand] private Task CompleteAsync(SessionRow row) => SetAsync(row, Status.Completed);
    [RelayCommand] private Task NoShowAsync(SessionRow row) => SetAsync(row, Status.NoShow);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsShowingQr))] private SessionRow? qrSession;
    public bool IsShowingQr => QrSession is not null;
    [RelayCommand] private void ShowQr(SessionRow row) => QrSession = QrSession == row ? null : row;
    [RelayCommand] private void CloseQr() => QrSession = null;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCancelling))] private SessionRow? cancellingSession;
    public bool IsCancelling => CancellingSession is not null;
    [ObservableProperty] private string cancelReason = "";

    [RelayCommand]
    private async Task CancelAsync(SessionRow row)
    {
        Notice = null;
        if (row.IsMine)
        {
            CancellingSession = row;
            CancelReason = "";
            return;
        }
        if (!await Confirm.AskAsync("Cancel this session?", $"With {row.With}, {Fmt.Range(row.Session.ScheduledStart, row.Session.ScheduledEnd)}.\nThey will be told.", "Cancel session")) return;
        if (await RunAsync(() => svc.CancelSessionAsync(row.Session.Id))) { Notice = "Session cancelled. The other person has been told."; await LoadAsync(); }
    }

    [RelayCommand]
    private async Task ConfirmCancelAsync()
    {
        if (CancellingSession is not { } row) return;
        if (string.IsNullOrWhiteSpace(CancelReason)) { ErrorMessage = "Say why you are cancelling this session."; return; }
        Notice = null;
        if (!await RunAsync(() => svc.CancelSessionAsync(row.Session.Id, CancelReason))) return;
        CancellingSession = null; CancelReason = "";
        Notice = "Session cancelled. The member has been told.";
        await LoadAsync();
    }

    [RelayCommand] private void KeepSession() { CancellingSession = null; CancelReason = ""; }

    [RelayCommand]
    private async Task ExtendAsync(SessionRow row)
    {
        Notice = null;
        if (row.ReservationId is not { } id || !await Confirm.AskAsync("Add 30 minutes?", $"With {row.With}. The member will be told.", "Add 30 minutes")) return;
        if (await RunAsync(() => svc.ExtendAsync(id, 30))) { Notice = "Added 30 minutes. The member has been told."; await LoadAsync(); }
    }

    [RelayCommand]
    private void StartReschedule(SessionRow row)
    {
        Notice = null; Rescheduling = row;
        var start = row.Session.ScheduledStart.ToLocalTime();
        Date = start.Date; Time = start.TimeOfDay;
    }

    [RelayCommand] private void StopReschedule() => Rescheduling = null;

    [RelayCommand]
    private async Task SendRescheduleAsync()
    {
        if (Rescheduling is not { } row) return;
        var start = (Date.Date + Time).ToUniversalTime();
        var end = start + (row.Session.ScheduledEnd - row.Session.ScheduledStart); // keep the session length
        Notice = null;
        if (!await Confirm.AskAsync("Ask for this new time?", $"With {row.With}, {Fmt.Range(start, end)}.\nIt moves once they approve.", "Send")) return;
        if (await RunAsync(() => svc.RescheduleAsync(row.Session.Id, start, end)))
        { Notice = "Reschedule sent. It moves once the other person approves it in Time requests."; Rescheduling = null; }
    }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReportingDamage))] private SessionRow? reportingDamageSession;
    public bool IsReportingDamage => ReportingDamageSession is not null;
    [ObservableProperty] private bool damageFound;
    [ObservableProperty] private string damageDescription = "";

    async Task SetAsync(SessionRow row, string status)
    {
        var (title, button) = status == Status.NoShow ? ("Mark this session as a no-show?", "Mark no-show") : ("Mark this session completed?", "Mark completed");
        if (!await Confirm.AskAsync(title, $"With {row.With}, {Fmt.Range(row.Session.ScheduledStart, row.Session.ScheduledEnd)}.", button)) return;
        if (!await RunAsync(() => svc.SetSessionStatusAsync(row.Session.Id, status))) return;
        await LoadAsync();
        if (status == Status.Completed && row.IsMine)
        {
            ReportingDamageSession = row;
            DamageFound = false;
            DamageDescription = "";
        }
    }

    [RelayCommand] private void StartDamageReport() => DamageFound = true;
    [RelayCommand] private void DismissDamage() { ReportingDamageSession = null; DamageFound = false; DamageDescription = ""; }

    [RelayCommand]
    private async Task SubmitDamageAsync()
    {
        if (ReportingDamageSession is not { } row) return;
        if (string.IsNullOrWhiteSpace(DamageDescription)) { ErrorMessage = "Describe what was broken or damaged."; return; }
        Notice = null;
        if (!await RunAsync(() => svc.ReportDamageAsync(row.Session.Id, null, DamageDescription), _ => { })) return;
        ReportingDamageSession = null; DamageFound = false; DamageDescription = "";
        Notice = "Damage report sent to admins.";
    }

    // ---- E5/E6/E7 (0120): Member Book a coach flow ----
    public sealed record HourOption(int Hour, string Label);
    [ObservableProperty] private bool isBookingCoach;
    [ObservableProperty] private DateTime bookDay = DateTime.Today;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PackageCoachText))] private int bookHours = 1;
    [ObservableProperty] private HourOption? bookHour;
    [ObservableProperty] private FreeCoachSlot? selectedFreeCoach;
    [ObservableProperty] private string bookCode = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PackageCoachText))] private MembershipPackage? currentPackage;
    public ObservableCollection<FreeCoachSlot> FreeCoaches { get; } = new();
    public ObservableCollection<HourOption> AvailableHours { get; } = new();
    public bool CanShowBookCoach => membership is not null;

    public string PackageCoachText => CurrentPackage is null
        ? "Needs an active membership covering the session."
        : CurrentPackage.IncludesCoach ? "Included in your package (free)"
        : CurrentPackage.CoachAddonPrice is { } p ? $"Buy the Coach access add-on (₱{p:N0} full term) on Packages to book a coach."
        : "Your current package doesn't include a coach or add-on.";

    partial void OnBookDayChanged(DateTime value) { if (IsBookingCoach) _ = LoadFreeCoachesAsync(); }
    partial void OnSelectedFreeCoachChanged(FreeCoachSlot? value) => RefreshHours();

    void RefreshHours()
    {
        var prev = BookHour?.Hour;
        AvailableHours.Clear();
        if (SelectedFreeCoach is { } c)
        {
            foreach (var h in c.FreeHours)
            {
                if (BookHours == 2 && !c.FreeHours.Contains(h + 1)) continue;
                var label = $"{DateTime.Today.AddHours(h):h tt} – {DateTime.Today.AddHours(h + BookHours):h tt}";
                AvailableHours.Add(new HourOption(h, label));
            }
        }
        BookHour = prev is { } p ? AvailableHours.FirstOrDefault(x => x.Hour == p) ?? AvailableHours.FirstOrDefault() : AvailableHours.FirstOrDefault();
    }

    [RelayCommand]
    private void SetBookHours(object? hours)
    {
        BookHours = int.TryParse(hours?.ToString(), out var parsed) && parsed == 2 ? 2 : 1;
        RefreshHours();
    }

    [RelayCommand]
    private async Task OpenBookCoachAsync()
    {
        IsBookingCoach = !IsBookingCoach;
        if (IsBookingCoach)
        {
            await LoadPackageCoachInfoAsync();
            await LoadFreeCoachesAsync();
        }
    }

    async Task LoadPackageCoachInfoAsync()
    {
        if (membership is null) return;
        var my = await membership.MyMembershipsAsync();
        var pkgs = await membership.ListPackagesAsync();
        if (!my.Ok || !pkgs.Ok || my.Value is not { } rows) { CurrentPackage = null; return; }
        var active = rows.FirstOrDefault(r => r.Status == Status.Active) ?? rows.FirstOrDefault();
        CurrentPackage = active is null ? null : pkgs.Value?.FirstOrDefault(p => p.Name == active.PackageName);
    }

    [RelayCommand]
    private Task LoadFreeCoachesAsync() => LoadFreeCoachesAsync(SelectedFreeCoach?.CoachId);

    public bool HasNoFreeCoaches => FreeCoaches.Count == 0;

    async Task LoadFreeCoachesAsync(string? preferCoachId)
    {
        var prevHour = BookHour?.Hour;
        var day = DateOnly.FromDateTime(BookDay.Date);
        var res = await svc.FreeCoachesAsync(day);
        if (!res.Ok) { ErrorMessage = ErrorText.For(res.Error, res.Message); return; }
        Fill(FreeCoaches, res.Value!);
        OnPropertyChanged(nameof(HasNoFreeCoaches));
        SelectedFreeCoach = (preferCoachId is not null ? FreeCoaches.FirstOrDefault(c => c.CoachId == preferCoachId) : null)
            ?? FreeCoaches.FirstOrDefault();
        if (prevHour is { } ph && AvailableHours.FirstOrDefault(h => h.Hour == ph) is { } match)
            BookHour = match;
    }

    [RelayCommand]
    private async Task BookCoachAsync()
    {
        ErrorMessage = Notice = null;
        if (SelectedFreeCoach is not { } coach || BookHour is not { } hr)
        {
            ErrorMessage = "Choose a coach and an available hour.";
            return;
        }
        var manilaOffset = TimeSpan.FromHours(8);
        var startUtc = new DateTimeOffset(BookDay.Date.AddHours(hr.Hour), manilaOffset).UtcDateTime;
        var res = await svc.BookCoachAsync(coach.CoachId, startUtc, BookHours, string.IsNullOrWhiteSpace(BookCode) ? null : BookCode.Trim());
        if (!res.Ok)
        {
            ErrorMessage = ErrorText.For(res.Error, res.Message);
            if (res.Error == AppErrorKind.Conflict)
                await LoadFreeCoachesAsync(coach.CoachId);   // keep day & hour, reload free coaches
            return;
        }
        var booked = res.Value!;
        if (booked.IsFree || booked.Amount <= 0)
        {
            Notice = $"Booked {coach.FullName} on {BookDay:ddd d MMM} at {hr.Label}.";
            IsBookingCoach = false;
            await LoadAsync();
            return;
        }
        Notice = $"Session held for 15 minutes (code {booked.PaymentCode}, ₱{booked.Amount:N2}). Pay online to confirm.";
        if (membership is not null && booked.PaymentId is { } pid)
        {
            var pay = await membership.PayOnlineAsync(pid);
            if (pay.Ok && pay.Value!.Url is { } url && opener is not null)
                await opener.OpenAsync(url);
        }
        await LoadAsync();
    }
}

/// <summary>One day of the week view with its sessions inline.</summary>
public sealed record WeekRow(CalendarDay Day, IReadOnlyList<CalendarSession> Sessions)
{
    public string Title => Day.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    public bool IsEmpty => Sessions.Count == 0;   // an empty day is one line in the week, not a card saying "No sessions."
}

// the coach's attendance calendar: a month or a week, a day list underneath, and marks that can be corrected in place
/// <summary>The coach dashboard (D10): this month's numbers, the month/week calendar with attendance marks, and shift posting for the selected day.
/// Without a coach service (older tests) it is the attendance calendar alone.</summary>
public partial class AttendanceViewModel(ISessionService sessions, IAttendanceService attendance, ICoachService? coach = null, IMembershipService? gym = null, IAmenityService? amenities = null) : LoadableViewModel
{
    /// <summary>The moment "now" is; tests set a fixed one.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    /// <summary>Days are the coach's local days. Tests set a fixed zone.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public ObservableCollection<CalendarDay> Days { get; } = new();          // the month grid, 42 cells
    public ObservableCollection<WeekRow> WeekRows { get; } = new();          // the week view, 7 rows
    public ObservableCollection<CalendarSession> DaySessions { get; } = new();
    public ObservableCollection<ShiftPost> DayShifts { get; } = new();
    public ObservableCollection<string> MyAmenities { get; } = new();
    public bool HasAmenities => MyAmenities.Count > 0;
    public int[] HoursOptions { get; } = [2, 4, 6, 8, 10];

    [ObservableProperty] private TimeSpan postTime = new(8, 0, 0);   // the start, as the coach's own clock shows it
    [ObservableProperty] private int postHours = 4;
    [ObservableProperty] private string? postNote;
    [ObservableProperty] private bool canPost;                       // the selected day still has gym time ahead
    [ObservableProperty] private string closedNote = "";              // G17 (0065): why no shift can be posted that day
    public bool HasShifts => coach is not null;
    List<ShiftPost> shifts = [];
    GymSettings? hours;
    IReadOnlyDictionary<DateOnly, string> closed = new Dictionary<DateOnly, string>();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMonth), nameof(IsWeek), nameof(PeriodTitle))] private string viewMode = "Month";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PeriodTitle))] private DateOnly focus;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayTitle), nameof(HasDay))] private DateOnly? selectedDay;
    [ObservableProperty] private string sessionsText = "—";
    [ObservableProperty] private string rateText = "—";
    [ObservableProperty] private string upcomingText = "—";
    [ObservableProperty] private string nextTimeText = "—";
    [ObservableProperty] private bool hasNext;     // "None booked" is words, not a big number
    [ObservableProperty] private string nextWhenText = "";
    [ObservableProperty] private string traineesText = "—";

    List<CalendarSession> loaded = [];      // what the server last said for the range in view
    List<CalendarSession> upcoming = [];
    int loadTicket;
    bool known;                             // the summary is only a fact once the month in view has really loaded; until then it says "—"

    public bool IsMonth => ViewMode == "Month";
    public bool IsWeek => ViewMode == "Week";
    public bool HasDay => SelectedDay is not null;
    public string DayTitle => SelectedDay is { } d ? d.ToString("dddd d MMMM", CultureInfo.InvariantCulture) : "";
    public string PeriodTitle => IsMonth ? Focus.ToString("MMMM yyyy", CultureInfo.InvariantCulture) : WeekTitle();

    string WeekTitle()
    {
        var w = AttendanceCalendar.WeekOf(Focus);
        return w[0].Month == w[6].Month
            ? $"{w[0].Day} to {w[6].Day} {w[6].ToString("MMM", CultureInfo.InvariantCulture)}"
            : $"{w[0].Day} {w[0].ToString("MMM", CultureInfo.InvariantCulture)} to {w[6].Day} {w[6].ToString("MMM", CultureInfo.InvariantCulture)}";
    }

    DateOnly Today => AttendanceCalendar.LocalDay(Clock(), Zone);

    public override async Task OnAppearingAsync()
    {
        if (Focus == default) { Focus = Today; SelectedDay = Today; }
        var gymTask = coach is not null && hours is null && gym is not null ? gym.GymAsync() : null;
        var closedTask = coach is not null && gym is not null ? gym.ClosedDaysAsync() : null;
        var traineesTask = coach is not null ? RunAsync(coach.TraineesAsync, t => TraineesText = t.Count.ToString()) : null;
        var reloadTask = ReloadAsync();
        await Task.WhenAll(new[] { gymTask, closedTask, traineesTask, reloadTask }.OfType<Task>());
        if (gymTask?.Result is { Ok: true, Value: { } g }) hours = g;
        if (closedTask?.Result is { Ok: true, Value: { } c }) closed = c;
        Prefill();
    }

    /// <summary>The form for the selected day: opening time, or the next half hour today; Post shift is off when no gym time is left.</summary>
    void Prefill()
    {
        var start = hours is not null && SelectedDay is { } day ? ShiftSlot.DefaultStart(day, hours.OpensAt, hours.ClosesAt, Clock()) : null;
        ClosedNote = SelectedDay is { } sd && closed.TryGetValue(sd, out var why) ? $"The gym is closed this day: {why}." : "";
        CanPost = start is not null && ClosedNote == "";
        if (start is { } s) PostTime = AttendanceCalendar.LocalTime(s, Zone).TimeOfDay;
        PostHours = 4;
    }

    [RelayCommand]
    private async Task PostShiftAsync()
    {
        if (coach is null || hours is null || SelectedDay is not { } day) return;
        Notice = null;
        var start = AttendanceCalendar.StartOfDayUtc(day, Zone) + PostTime;
        var end = ShiftSlot.End(start, PostHours, hours.ClosesAt);
        if (end <= start) { ErrorMessage = "That start is at or after closing time. Pick an earlier start."; return; }
        if (!await RunAsync(() => coach.PostShiftAsync(start, end, PostNote))) return;
        Notice = end < start.AddHours(PostHours)
            ? $"Shift posted until closing at {AttendanceCalendar.LocalTime(end, Zone).ToString("h:mm tt", CultureInfo.InvariantCulture)}."
            : "Shift posted. Your trainees can see it on the Coaches page.";
        PostNote = null;
        await ReloadAsync();
        Prefill();
    }

    [RelayCommand]
    private async Task CancelShiftAsync(ShiftPost shift)
    {
        if (coach is null) return;
        Notice = null;
        if (!await Confirm.AskAsync("Cancel this shift?", Fmt.Range(shift.Start, shift.End), "Cancel shift")) return;
        if (await RunAsync(() => coach.CancelShiftAsync(shift.Id))) { Notice = "Shift cancelled."; await ReloadAsync(); }
    }

    // one range covers both views of the same date (the month grid and its week), so switching between them needs no new load
    (DateTime From, DateTime To) Range()
    {
        var grid = AttendanceCalendar.MonthGrid(Focus.Year, Focus.Month); var week = AttendanceCalendar.WeekOf(Focus);
        var first = grid[0] < week[0] ? grid[0] : week[0];
        var last = grid[^1] > week[6] ? grid[^1] : week[6];
        return (AttendanceCalendar.StartOfDayUtc(first, Zone), AttendanceCalendar.StartOfDayUtc(last.AddDays(1), Zone));
    }

    async Task ReloadAsync()
    {
        var ticket = ++loadTicket;                       // the newest request wins: a slow answer for a period the coach has left is dropped
        ErrorMessage = null;
        var (from, to) = Range();
        var calendar = sessions.CalendarAsync(from, to);
        var coming = sessions.UpcomingAsync();
        var posted = coach?.MyShiftsAsync() ?? Task.FromResult(AppResult<IReadOnlyList<ShiftPost>>.Success([]));
        var board = amenities?.BoardAsync();
        await Task.WhenAll(calendar, coming, posted, board ?? Task.CompletedTask);
        if (ticket != loadTicket) return;
        if (board is not null && (await board) is { Ok: true, Value: { } b })
        {
            Fill(MyAmenities, b.Cards.Where(c => c.RunsThis).Select(c => c.Amenity.Name).ToList());
            OnPropertyChanged(nameof(HasAmenities));
        }
        var cal = await calendar; var up = await coming;
        if ((await posted).Ok) shifts = (await posted).Value!.ToList();
        if (cal.Ok) { loaded = cal.Value!.ToList(); known = true; } else ErrorMessage = ErrorText.For(cal.Error, cal.Message);   // a failed look leaves what is shown as it was
        if (up.Ok) upcoming = up.Value!.ToList();
        Rebuild();
    }

    void Rebuild()
    {
        var today = Today;
        var month = AttendanceCalendar.Days(AttendanceCalendar.MonthGrid(Focus.Year, Focus.Month), loaded, today, SelectedDay, new DateOnly(Focus.Year, Focus.Month, 1), Zone);
        Sync(Days, AttendanceCalendar.WithEntries(month, loaded, shifts.Where(x => x.Status != "cancelled").Select(x => x.Start), Zone), d => d.Date, (a, b) => a == b);
        var week = AttendanceCalendar.Days(AttendanceCalendar.WeekOf(Focus), loaded, today, SelectedDay, null, Zone);
        Fill(WeekRows, week.Select(d => new WeekRow(d, AttendanceCalendar.SessionsOn(loaded, d.Date, Zone))));
        Fill(DaySessions, SelectedDay is { } day ? AttendanceCalendar.SessionsOn(loaded, day, Zone) : Array.Empty<CalendarSession>());
        Fill(DayShifts, SelectedDay is { } sd ? shifts.Where(x => AttendanceCalendar.LocalDay(x.Start, Zone) == sd).OrderBy(x => x.Start) : []);

        var summary = AttendanceCalendar.Summary(AttendanceCalendar.SessionsInMonth(loaded, Focus.Year, Focus.Month, Zone), upcoming);
        SessionsText = known ? summary.SessionsThisMonth.ToString(CultureInfo.InvariantCulture) : "—";   // not a made-up zero while the month is loading or could not load
        RateText = known && summary.RatePercent is { } r ? $"{r}%" : "—";
        UpcomingText = summary.Upcoming.ToString(CultureInfo.InvariantCulture);
        // the yellow card (9.6, as approved): the time big, then the day in words and who
        HasNext = summary.Next is not null;
        if (summary.Next is { } next)
        {
            var at = AttendanceCalendar.LocalTime(next.Session.ScheduledStart, Zone);
            var on = DateOnly.FromDateTime(at);
            NextTimeText = at.ToString("h:mm tt", CultureInfo.InvariantCulture);
            NextWhenText = $"{(on == today ? "today" : on == today.AddDays(1) ? "tomorrow" : at.ToString("ddd d MMM", CultureInfo.InvariantCulture))} with {next.With}";
        }
        else { NextTimeText = "None booked"; NextWhenText = "Sessions you book show here."; }
    }

    [RelayCommand] private void ShowMonth() { ViewMode = "Month"; Rebuild(); }
    [RelayCommand] private void ShowWeek() { ViewMode = "Week"; Rebuild(); }

    [RelayCommand] private Task PreviousAsync() => MoveAsync(IsMonth ? Focus.AddMonths(-1) : Focus.AddDays(-7));
    [RelayCommand] private Task NextAsync() => MoveAsync(IsMonth ? Focus.AddMonths(1) : Focus.AddDays(7));
    [RelayCommand] private async Task TodayAsync() { await MoveAsync(Today, select: Today); Prefill(); }

    async Task MoveAsync(DateOnly to, DateOnly? select = null)
    {
        Focus = to; SelectedDay = select;
        known = false;                                    // days keep the marks the old data has for their own dates; the month's numbers wait for the new answer
        Rebuild();
        await ReloadAsync();
    }

    [RelayCommand]
    private void SelectDay(CalendarDay day) { SelectedDay = day.Date; Rebuild(); Prefill(); }

    // the argument is "<sessionId>|<mark>" so one XAML button template serves all four marks
    [RelayCommand]
    private async Task MarkAsync(string arg)
    {
        var parts = arg.Split('|');
        var id = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var row = loaded.FirstOrDefault(s => s.Session.Id == id);
        if (row is null || !row.CanMark) return;          // a cancelled session cannot be marked
        Notice = null; ErrorMessage = null;
        var result = await attendance.RecordAsync(id, parts[1]);
        if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        Notice = $"Marked {row.With} as {parts[1]}.";
        await ReloadAsync();
    }
}

// ---------- admin ----------
public partial class UsersViewModel(IAdminService svc, IAccountArchiveService? archive = null) : LoadableViewModel
{
    public ObservableCollection<UserRow> Users { get; } = new();
    IReadOnlyList<UserRow> active = [];
    // item 3 (0059): active accounts with a first name and no last name, for a system admin to correct
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasIncomplete), nameof(IncompleteText))] private int incompleteCount;
    [ObservableProperty] private bool onlyIncomplete;
    public bool HasIncomplete => IncompleteCount > 0;
    public string IncompleteText => $"Name incomplete ({IncompleteCount})";
    partial void OnOnlyIncompleteChanged(bool value) => ShowUsers();
    [RelayCommand] private void ToggleIncomplete() => OnlyIncomplete = !OnlyIncomplete;
    void ShowUsers()
    {
        IncompleteCount = active.Count(u => u.NameIncomplete);
        if (IncompleteCount == 0) OnlyIncomplete = false;
        Fill(Users, OnlyIncomplete ? active.Where(u => u.NameIncomplete) : active);
    }
    /// <summary>Switched off but not deleted: they can be switched back on here.</summary>
    public ObservableCollection<UserRow> Deactivated { get; } = new();
    /// <summary>What can be added: a sub-admin adds members and coaches only (D15).</summary>
    [ObservableProperty] private string[] roles = ["Member", "Coach"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddTitle))] private bool isSuperAdmin;
    public string AddTitle => IsSuperAdmin ? "Add a member, coach or staff account" : "Add a member or coach";

    /// <summary>The super admin manages anyone; a sub-admin manages members and coaches.</summary>
    public bool CanManage(UserRow user) => IsSuperAdmin || !user.IsStaff;
    [ObservableProperty] private UserRow? resetting;
    [ObservableProperty] private string resetPassword = "";
    [ObservableProperty] private string newFirstName = "";
    [ObservableProperty] private string newLastName = "";
    [ObservableProperty] private string newEmail = "";
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string newRole = "Member";

    public override Task OnAppearingAsync() => LoadAsync();

    [ObservableProperty] private bool canDeleteAccounts;

    public AccountDeletionViewModel? DeletionFor(UserRow user) => CanDeleteAccounts && archive is not null
        ? new AccountDeletionViewModel(archive, user) : null;

    [RelayCommand]
    private async Task LoadAsync()
    {
        CanDeleteAccounts = false;
        var deleteTask = archive is not null ? RunAsync(archive.CanManageAsync, allowed => CanDeleteAccounts = allowed) : Task.CompletedTask;
        var superTask = RunAsync(svc.IsSuperAdminAsync, yes => IsSuperAdmin = yes);
        var usersTask = RunAsync(svc.UsersAsync, rows => { active = rows.Where(r => r.IsActive).ToList(); ShowUsers(); Fill(Deactivated, rows.Where(r => !r.IsActive)); });
        await Task.WhenAll(deleteTask, superTask, usersTask);
        Roles = IsSuperAdmin ? ["Member", "Coach", "Front desk", "Admin"] : ["Member", "Coach"];
        if (!Roles.Contains(NewRole)) NewRole = "Member";
    }

    [RelayCommand]
    private async Task AddUserAsync()
    {
        Notice = null;
        if (UserRow.RoleCode(NewRole) is not { } role || !Roles.Contains(NewRole)) { ErrorMessage = "Choose a role from the list."; return; }
        if (!await Confirm.AskAsync($"Add {NameParts.Join(NewFirstName, NewLastName)}?", $"{NewEmail.Trim()}\nRole: {NewRole}\nThey sign in with the temporary password, then pick their own.", "Add user")) return;
        if (!await RunAsync(() => svc.CreateUserAsync(NewFirstName, NewLastName, NewEmail, role, NewPassword))) return;
        Notice = $"{NameParts.Join(NewFirstName, NewLastName)} can sign in now with that email and password.";
        NewFirstName = NewLastName = NewEmail = NewPassword = "";
        await LoadAsync();
    }

    [RelayCommand] private void StartReset(UserRow user) { Resetting = user; ResetPassword = ""; Notice = null; Correcting = null; }
    [RelayCommand] private void StopReset() => Resetting = null;

    [RelayCommand]
    private async Task ConfirmResetAsync()
    {
        if (Resetting is not { } user) return;
        if (!await RunAsync(() => svc.ResetPasswordAsync(user.UserId, ResetPassword))) return;
        Notice = $"{user.Username} can sign in with the new temporary password.";
        Resetting = null;
    }

    // ---------- correcting a locked name ----------
    [ObservableProperty] private UserRow? correcting;
    [ObservableProperty] private string correctFirstName = "";
    [ObservableProperty] private string correctLastName = "";
    [ObservableProperty] private string correctReason = "";

    public bool CanCorrectName(UserRow user) => IsSuperAdmin && user.IsActive;

    [RelayCommand]
    private void StartCorrectName(UserRow user)
    {
        (Correcting, CorrectFirstName, CorrectLastName, CorrectReason) = (user, user.FirstName, user.LastName, "");
        Notice = null; ErrorMessage = null; Resetting = null;
    }
    [RelayCommand] private void StopCorrectName() => Correcting = null;

    [RelayCommand]
    private async Task ConfirmCorrectNameAsync()
    {
        if (Correcting is not { } user) return;
        Notice = null;
        if (!await RunAsync(() => svc.CorrectNameAsync(user.UserId, CorrectFirstName, CorrectLastName, CorrectReason))) return;
        Correcting = null;
        await LoadAsync();
        Notice = $"Name corrected to {NameParts.Join(CorrectFirstName.Trim(), CorrectLastName.Trim())}.";
    }

    // argument is "<userId>|<role>"; assigns the role if missing, otherwise revokes it
    [RelayCommand]
    private async Task ToggleRoleAsync(string arg)
    {
        var parts = arg.Split('|');
        var user = Users.First(u => u.UserId == parts[0]);
        var name = UserRow.RoleName(parts[1]);
        if (!await (user.Roles.Contains(parts[1])
                ? Confirm.AskAsync($"Remove the {name} role from {user.Username}?", "They lose what that role lets them do.", "Remove role")
                : Confirm.AskAsync($"Give {user.Username} the {name} role?", "They get everything that role lets them do.", "Give role"))) return;
        var ok = user.Roles.Contains(parts[1])
            ? await RunAsync(() => svc.RevokeRoleAsync(user.UserId, parts[1]))
            : await RunAsync(() => svc.AssignRoleAsync(user.UserId, parts[1]));
        if (ok) await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(UserRow user)
    {
        if (!await (user.IsActive ? Confirm.AskAsync($"Deactivate {user.Username}?", "They can't sign in until you reactivate the account.", "Deactivate")
                                  : Confirm.AskAsync($"Reactivate {user.Username}?", "They can sign in again.", "Reactivate"))) return;
        if (await RunAsync(() => svc.SetActiveAsync(user.UserId, !user.IsActive))) await LoadAsync();
    }
}

/// <summary>Phase 4: one package on Packages and amenities. A draft version sits right under the package it will replace.</summary>
public sealed record CatalogPackageRow(MembershipPackage Package, string? PhotoUrl, bool IsVersion, bool HasDraftVersion)
{
    public string StateText => Package.State == CatalogState.Draft ? "Draft" : "On sale";
    public string PriceText => Package.Price > 0 ? $"₱{Package.Price:N2}" : "Not set";
    public string Subtitle => IsVersion ? "New version, replaces the one above when published"
        : string.IsNullOrWhiteSpace(Package.Description) ? "No description yet" : Package.Description!;
}

public sealed record CatalogAmenityRow(Amenity Amenity, string? PhotoUrl)
{
    public string StateText => Amenity.State == CatalogState.Draft ? "Draft" : "On sale";
    public string SoldAsText => Amenity.Price <= 0 ? "Price not set" : Amenity.DurationDays is { } d ? $"₱{Amenity.Price:N2} for {d} days" : $"₱{Amenity.Price:N2} per hour";   // 0090
    public string Subtitle => string.IsNullOrWhiteSpace(Amenity.Description) ? "No description yet" : Amenity.Description!;
}

/// <summary>One benefit line in the side panel (packages only).</summary>
public sealed partial class BenefitLine : ObservableObject { [ObservableProperty] private string text = ""; }

/// <summary>
/// Packages and amenities (G6 to G9, approved mockup docs/mockups/catalog-add.html): one Actions menu per row offering only what the row's
/// state allows, and one side panel that adds, edits, saves as a draft, puts on sale, and compares a new version before it is published.
/// </summary>
public partial class CatalogAdminViewModel(IAdminService svc, IProofPicker? picker = null) : LoadableViewModel
{
    /// <summary>Only the super admin archives (D15).</summary>
    [ObservableProperty] private bool canArchive;

    public ObservableCollection<CatalogPackageRow> Packages { get; } = new();
    public ObservableCollection<CatalogAmenityRow> Amenities { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        var adminTask = RunAsync(svc.IsSuperAdminAsync, yes => CanArchive = yes);
        var pkgTask = svc.AllPackagesAsync();
        var amTask = svc.AllAmenitiesAsync();
        await Task.WhenAll(adminTask, pkgTask, amTask);

        if (pkgTask.Result is { Ok: true, Value: { } packages })
        {
            var live = packages.Where(p => p.IsActive).OrderBy(p => p.Id).ToList();
            var entries = new List<(MembershipPackage Pkg, bool IsVersion, bool HasDraft)>();
            foreach (var p in live.Where(p => p.PublishedAt is not null))
            {
                var version = live.FirstOrDefault(v => v.ReplacesId == p.Id && v.PublishedAt is null);
                entries.Add((p, false, version is not null));
                if (version is not null) entries.Add((version, true, false));
            }
            foreach (var d in live.Where(d => d.PublishedAt is null && !entries.Any(e => e.Pkg.Id == d.Id)))
                entries.Add((d, false, false));

            var photos = await Task.WhenAll(entries.Select(e => SignedPhoto(e.Pkg.ImagePath)));
            var rows = entries.Select((e, i) => new CatalogPackageRow(e.Pkg, photos[i], e.IsVersion, e.HasDraft)).ToList();
            Fill(Packages, rows);
        }
        if (amTask.Result is { Ok: true, Value: { } amenities })
        {
            var live = amenities.Where(a => a.IsActive).OrderBy(a => a.PublishedAt is null).ThenBy(a => a.Id).ToList();
            var photos = await Task.WhenAll(live.Select(a => SignedPhoto(a.ImagePath)));
            var rows = live.Select((a, i) => new CatalogAmenityRow(a, photos[i])).ToList();
            Fill(Amenities, rows);
        }
    }

    async Task<string?> SignedPhoto(string? path) => path is null ? null : (await svc.CatalogPhotoUrlAsync(path)).Value;

    // ---------- the Actions menu ----------
    public IReadOnlyList<string> ActionsFor(object row) => row switch
    {
        CatalogPackageRow { Package.State: CatalogState.Draft, IsVersion: true } => ["Edit draft", "Review and publish", "Delete draft"],
        CatalogPackageRow { Package.State: CatalogState.Draft } => ["Edit draft", "Add to sale", "Delete draft"],
        CatalogPackageRow r => [.. r.HasDraftVersion ? [] : new[] { "Create new version" }, .. CanArchive ? new[] { "Archive" } : []],
        CatalogAmenityRow { Amenity.State: CatalogState.Draft } => ["Edit draft", "Add to sale", "Delete draft"],
        CatalogAmenityRow r => ["Edit", .. r.Amenity.DurationDays is null ? [] : new[] { "Sell per session" }, .. CanArchive ? new[] { "Archive" } : []],
        _ => []
    };

    [RelayCommand]
    private async Task RunActionAsync((object Row, string Action) pick)
    {
        ErrorMessage = Notice = null;
        switch (pick.Row, pick.Action)
        {
            case (CatalogPackageRow r, "Edit draft"): OpenPackage(r.Package); break;
            case (CatalogPackageRow r, "Add to sale" or "Review and publish"): OpenPackage(r.Package); await MainAsync(); break;
            case (CatalogPackageRow r, "Create new version"):
                var newId = 0;
                if (!await RunAsync(() => svc.NewPackageVersionAsync(r.Package.Id), id => newId = id)) return;
                OpenPackage(new MembershipPackage
                {
                    Id = newId, Name = r.Package.Name, Description = r.Package.Description, Price = r.Package.Price,
                    DurationDays = r.Package.DurationDays, IsDayPass = r.Package.IsDayPass, IncludesCoach = r.Package.IncludesCoach,
                    CoachAddonPrice = r.Package.CoachAddonPrice, Benefits = [.. r.Package.Benefits], ImagePath = r.Package.ImagePath,
                    ReplacesId = r.Package.Id, IsActive = true, PublishedAt = null
                });
                _ = LoadAsync();
                break;
            case (CatalogPackageRow r, "Delete draft"): await DeleteDraftAsync("plan", r.Package.Id, r.Package.Name); break;
            case (CatalogPackageRow r, "Archive"): Deleting = r.Package; break;
            case (CatalogAmenityRow r, "Edit draft" or "Edit"): OpenAmenity(r.Amenity); break;
            case (CatalogAmenityRow r, "Add to sale"): OpenAmenity(r.Amenity); await MainAsync(); break;
            case (CatalogAmenityRow r, "Sell per session"): StartSessionPrice(r.Amenity); break;
            case (CatalogAmenityRow r, "Delete draft"): await DeleteDraftAsync("amenity", r.Amenity.Id, r.Amenity.Name); break;
            case (CatalogAmenityRow r, "Archive"): DeletingAmenity = r.Amenity; break;
        }
    }

    async Task DeleteDraftAsync(string kind, int id, string name)
    {
        if (!await Confirm.AskAsync($"Delete the draft {name}?", "It has never been on sale, so it goes for good.", "Delete draft")) return;
        if (!await RunAsync(() => svc.DeleteDraftAsync(kind, id))) return;
        Notice = $"Deleted the draft {name}.";
        await LoadAsync();
    }

    // ---------- the side panel ----------
    [ObservableProperty] private bool isPanelOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PriceLabel))] private bool isPackagePanel;
    [ObservableProperty] private bool isComparing;
    [ObservableProperty] private string panelTitle = "";
    [ObservableProperty] private string panelSubtitle = "";
    [ObservableProperty] private string mainLabel = "Add";
    [ObservableProperty] private bool canSaveDraft = true;
    [ObservableProperty] private string? versionNote;

    [ObservableProperty] private string formName = "";
    [ObservableProperty] private string formDescription = "";
    [ObservableProperty] private decimal formPrice;
    [ObservableProperty] private int formDays = 30;
    [ObservableProperty] private bool formDayPass;   // 0098: the Day pass package (its term ends at midnight)
    [ObservableProperty] private bool formIncludesCoach;   // 0119: E3
    [ObservableProperty] private string formCoachAddonPrice = "";   // 0119: E4; blank = no add-on
    partial void OnFormIncludesCoachChanged(bool value) { if (value) FormCoachAddonPrice = ""; }
    /// <summary>G19 (0066): a new amenity is sold per session; one still set up as a pass keeps its days until Sell per session.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PriceLabel))] private bool isOldPass;
    public string PriceLabel => IsPackagePanel ? "Price" : IsOldPass ? "Price for the pass" : "Price per hour";
    public ObservableCollection<BenefitLine> Benefits { get; } = new();
    [ObservableProperty] private string newBenefit = "";

    [ObservableProperty] private string? photoUrl;
    [ObservableProperty] private string? photoText;
    (string FileName, byte[] Data)? _pendingPhoto;

    [ObservableProperty] private string? missingText;
    [ObservableProperty] private bool nameMissing, descriptionMissing, priceMissing, daysMissing;

    [ObservableProperty] private IReadOnlyList<PackageChange> changes = [];
    [ObservableProperty] private string? renewalNote;

    MembershipPackage? _package;   // the package or draft in the panel (Id 0 = not saved yet)
    Amenity? _amenity;

    [RelayCommand] private void AddPackage() => OpenPackage(new MembershipPackage { DurationDays = 30 });
    [RelayCommand] private void AddAmenity() => OpenAmenity(new Amenity());

    void Open(bool package, string name, string? description, decimal price, string? photo)
    {
        ErrorMessage = Notice = null;
        (IsPackagePanel, IsComparing, FormName, FormDescription, FormPrice, NewBenefit, _pendingPhoto, PhotoText) = (package, false, name, description ?? "", price, "", null, null);
        ClearMissing();
        PhotoUrl = photo;
        IsPanelOpen = true;
    }

    void OpenPackage(MembershipPackage p)
    {
        (_package, _amenity) = (p, null);
        var replaces = p.ReplacesId is { } id ? Packages.FirstOrDefault(r => r.Package.Id == id)?.Package : null;
        (PanelTitle, PanelSubtitle, MainLabel) = replaces is not null
            ? ($"New version of {replaces.Name}", $"A draft. {replaces.Name} stays on sale until you publish this.", "Review and publish")
            : (p.Id == 0 ? "Add a package" : "Edit draft", "Save as draft to finish it later. Add puts it on sale.", "Add");
        CanSaveDraft = true;
        VersionNote = replaces is null ? null : $"This is a draft of a new version. {replaces.Name} stays on sale as it is until you publish this one. Discount codes for the old version don't carry over.";
        FormDays = p.DurationDays; FormDayPass = p.IsDayPass;
        FormIncludesCoach = p.IncludesCoach;
        FormCoachAddonPrice = p.CoachAddonPrice is { } cap ? cap.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "";
        Benefits.Clear();
        foreach (var b in p.Benefits) Benefits.Add(new BenefitLine { Text = b });
        Open(true, p.Name, p.Description, p.Price, Packages.FirstOrDefault(r => r.Package.Id == p.Id && p.Id != 0)?.PhotoUrl);
    }

    void OpenAmenity(Amenity a)
    {
        (_package, _amenity) = (null, a);
        var published = a.State == CatalogState.Published;
        (PanelTitle, PanelSubtitle, MainLabel, CanSaveDraft) = published
            ? ($"Edit {a.Name}", "Changes apply to new purchases. Passes and sessions already paid keep what was paid.", "Save changes", false)
            : (a.Id == 0 ? "Add an amenity" : "Edit draft", "Save as draft to finish it later. Add puts it on sale.", "Add", true);
        VersionNote = null;
        IsOldPass = a.DurationDays is not null;
        Benefits.Clear();
        Open(false, a.Name, a.Description, a.Price, Amenities.FirstOrDefault(r => r.Amenity.Id == a.Id && a.Id != 0)?.PhotoUrl);
    }

    [RelayCommand] private void ClosePanel() => (IsPanelOpen, IsComparing) = (false, false);
    [RelayCommand] private void BackToEdit() => IsComparing = false;

    [RelayCommand]
    private void AddBenefit()
    {
        if (string.IsNullOrWhiteSpace(NewBenefit)) return;
        Benefits.Add(new BenefitLine { Text = NewBenefit.Trim() });
        NewBenefit = "";
    }
    [RelayCommand] private void RemoveBenefit(BenefitLine line) => Benefits.Remove(line);

    [RelayCommand]
    private async Task PickPhotoAsync()
    {
        if (picker is null || await picker.PickAsync() is not { } file) return;
        _pendingPhoto = file;
        PhotoText = $"{file.FileName}, saved with the {(IsPackagePanel ? "package" : "amenity")}";
    }

    void ClearMissing() { MissingText = null; NameMissing = DescriptionMissing = PriceMissing = DaysMissing = false; }
    // a field's note goes as soon as it is filled in; the summary goes with the first change
    partial void OnFormNameChanged(string value) { NameMissing = false; MissingText = null; }
    partial void OnFormDescriptionChanged(string value) { DescriptionMissing = false; MissingText = null; }
    partial void OnFormPriceChanged(decimal value) { PriceMissing = false; MissingText = null; }
    partial void OnFormDaysChanged(int value) { DaysMissing = false; MissingText = null; }

    /// <summary>Add (and Review and publish) checks what publishing needs (0061's missing_fields) before anything is saved.</summary>
    bool Complete()
    {
        ClearMissing();
        (NameMissing, DescriptionMissing, PriceMissing, DaysMissing) = (string.IsNullOrWhiteSpace(FormName), string.IsNullOrWhiteSpace(FormDescription), FormPrice <= 0, IsPackagePanel && FormDays <= 0);
        var missing = new[] { (NameMissing, "a name"), (DescriptionMissing, "a description"), (PriceMissing, "a price"), (DaysMissing, "the days") }.Where(m => m.Item1).Select(m => m.Item2).ToList();
        if (missing.Count == 0) return true;
        var list = missing.Count == 1 ? missing[0] : string.Join(", ", missing.SkipLast(1)) + " and " + missing[^1];
        MissingText = $"Add needs {(missing.Count == 1 ? "1 more thing" : $"{missing.Count} more things")}: {list}. Or save it as a draft and finish later.";
        return false;
    }

    /// <summary>Writes the panel to its row (a new row is a draft) and then the chosen photo, which needs the row's id.</summary>
    async Task<bool> SaveAsync()
    {
        var description = string.IsNullOrWhiteSpace(FormDescription) ? null : FormDescription.Trim();
        bool ok;
        int id;
        if (IsPackagePanel)
        {
            decimal? addon = null;
            if (!FormIncludesCoach && !string.IsNullOrWhiteSpace(FormCoachAddonPrice))
            {
                if (!decimal.TryParse(FormCoachAddonPrice.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                {
                    MissingText = "Coach add-on price must be more than zero, or leave it blank.";
                    return false;
                }
                addon = parsed;
            }
            var p = _package!;
            (p.Name, p.Description, p.Price, p.DurationDays, p.IsDayPass, p.IncludesCoach, p.CoachAddonPrice) =
                (FormName.Trim(), description, FormPrice, FormDayPass ? 1 : FormDays, FormDayPass, FormIncludesCoach, FormIncludesCoach ? null : addon);
            p.Benefits = Benefits.Select(b => b.Text.Trim()).Where(b => b.Length > 0).ToList();
            ok = await RunAsync(() => svc.SavePackageAsync(p));
            id = p.Id;
        }
        else
        {
            var a = _amenity!;
            (a.Name, a.Description, a.Price) = (FormName.Trim(), description, FormPrice);
            ok = await RunAsync(() => svc.SaveAmenityAsync(a));
            id = a.Id;
        }
        if (!ok || _pendingPhoto is not { } photo) return ok;
        if (!await RunAsync(() => IsPackagePanel ? svc.SetPackagePhotoAsync(id, photo.FileName, photo.Data) : svc.SetAmenityPhotoAsync(id, photo.FileName, photo.Data), _ => { })) return false;
        _pendingPhoto = null;
        return true;
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        ClearMissing();
        if (string.IsNullOrWhiteSpace(FormName)) { (NameMissing, MissingText) = (true, "A draft needs a name."); return; }
        if (IsPackagePanel && FormDays <= 0) { (DaysMissing, MissingText) = (true, "Days must be more than zero."); return; }
        var ok = await SaveAsync();
        await LoadAsync();
        if (!ok) return;
        IsPanelOpen = false;
        Notice = $"Saved {FormName.Trim()} as a draft.";
    }

    /// <summary>Add, Review and publish, or Save changes, as the panel's main button says.</summary>
    [RelayCommand]
    private async Task MainAsync()
    {
        if (!Complete()) return;
        var terms = IsPackagePanel ? $"₱{FormPrice:N2} for {FormDays} days." : IsOldPass ? $"Pass: ₱{FormPrice:N2} for {_amenity!.DurationDays} days." : $"₱{FormPrice:N2} per hour.";
        var ask = (_package, _amenity) switch
        {
            ({ ReplacesId: not null }, _) => ((string, string, string)?)null,   // the comparison is the check
            (_, { State: CatalogState.Published }) => ($"Save changes to {FormName.Trim()}?", $"{terms} New purchases use the new details.", "Save changes"),
            (not null, _) => ($"Put {FormName.Trim()} on sale?", $"{terms} Once on sale it can't be changed; you make a new version instead.", "Add to sale"),
            _ => ($"Put {FormName.Trim()} on sale?", $"{terms} Members can buy it at once.", "Add to sale"),
        };
        if (ask is { } q && !await Confirm.AskAsync(q.Item1, q.Item2, q.Item3)) return;
        var saved = await SaveAsync();
        await LoadAsync();
        if (!saved) return;
        var name = FormName.Trim();
        if (_package is { ReplacesId: { } oldId } version)
        {
            if ((await svc.AllPackagesAsync()).Value?.FirstOrDefault(p => p.Id == oldId) is not { } old) return;
            Changes = PackageChanges.Between(old, version);
            RenewalNote = $"Members on the old version keep what they paid for until it ends. When they renew, they move to the new version at ₱{version.Price:N2}; the renewal screen says so.";
            IsComparing = true;
            return;
        }
        if (_amenity is { State: CatalogState.Published })
        {
            IsPanelOpen = false;
            Notice = $"Saved changes to {name}.";
            return;
        }
        if (!await RunAsync(() => svc.PublishAsync(IsPackagePanel ? "plan" : "amenity", IsPackagePanel ? _package!.Id : _amenity!.Id))) return;
        IsPanelOpen = false;
        await LoadAsync();
        Notice = $"{name} is on sale.";
    }

    [RelayCommand]
    private async Task PublishVersionAsync()
    {
        if (_package is not { ReplacesId: not null } version) return;
        if (!await RunAsync(() => svc.PublishAsync("plan", version.Id))) return;
        (IsPanelOpen, IsComparing) = (false, false);
        await LoadAsync();
        Notice = $"The new {version.Name} is on sale. The old one is archived; members on it keep their terms.";
    }

    // ---------- selling an amenity per session (D11) ----------
    [ObservableProperty] private Amenity? pricingAmenity;
    [ObservableProperty] private decimal sessionPrice;

    [RelayCommand] private void StartSessionPrice(Amenity a) { (PricingAmenity, SessionPrice) = (a, 0); ErrorMessage = null; }
    [RelayCommand] private void CancelSessionPrice() => PricingAmenity = null;

    [RelayCommand]
    private async Task SaveSessionPriceAsync()
    {
        if (PricingAmenity is not { } a) return;
        if (SessionPrice <= 0) { ErrorMessage = "Enter a session price above zero."; return; }
        if (!await Confirm.AskAsync($"Sell {a.Name} per session?", $"Members pay ₱{SessionPrice:N2} for each session. Passes already sold stay valid until they expire.", "Sell per session")) return;
        var (price, days) = (a.Price, a.DurationDays);
        (a.Price, a.DurationDays) = (SessionPrice, null);
        if (!await RunAsync(() => svc.SaveAmenityAsync(a))) { (a.Price, a.DurationDays) = (price, days); return; }
        PricingAmenity = null;
        Notice = $"{a.Name} now sells per session.";
        await LoadAsync();
    }

    // ---------- archive asks first (System admin only, D15) ----------
    [ObservableProperty] private MembershipPackage? deleting;
    [ObservableProperty] private Amenity? deletingAmenity;

    [RelayCommand] private void KeepAmenity() => DeletingAmenity = null;
    [RelayCommand] private void KeepPackage() => Deleting = null;

    [RelayCommand]
    private async Task ConfirmDeleteAmenityAsync()
    {
        if (DeletingAmenity is not { } a) return;
        DeletingAmenity = null;
        if (await RunAsync(() => svc.DeleteAmenityAsync(a))) await LoadAsync();
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (Deleting is not { } p) return;
        Deleting = null;
        if (await RunAsync(() => svc.DeletePackageAsync(p))) await LoadAsync();
    }
}

/// <summary>A package or amenity to tick on the new-code form (G3).</summary>
public partial class ItemChoice(string kind, int id, string name) : ObservableObject
{
    public string Kind { get; } = kind; public int Id { get; } = id; public string Name { get; } = name;
    [ObservableProperty] private bool isChosen;
}

public partial class DiscountsViewModel(IAdminService svc) : LoadableViewModel
{
    /// <summary>Only the super admin archives and deletes codes (D15).</summary>
    [ObservableProperty] private bool canArchive;
    /// <summary>Live codes: not archived, not expired. A code turned off for now stays here (G1).</summary>
    public ObservableCollection<DiscountRow> Discounts { get; } = new();
    [ObservableProperty] private string newCode = "";
    [ObservableProperty] private int newPercent = 10;
    [ObservableProperty] private DateTime validUntil = DateTime.Today.AddDays(30);
    [ObservableProperty] private int newLimit = 100;
    /// <summary>G4: on by default; off keeps a code private, for one person.</summary>
    [ObservableProperty] private bool newShowToMembers = true;
    // G3: what the new code fits
    [ObservableProperty] private bool allPackages;
    [ObservableProperty] private bool allAmenities;
    [ObservableProperty] private bool forCoachAddon;
    public ObservableCollection<ItemChoice> PackageChoices { get; } = new();
    public ObservableCollection<ItemChoice> AmenityChoices { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => Task.WhenAll(
        RunAsync(svc.IsSuperAdminAsync, yes => CanArchive = yes),
        RunAsync(svc.DiscountsAsync, rows => Fill(Discounts, rows.Where(r => r.Discount.IsActive && ManilaTime.Utc(r.Discount.ExpiresAt) > DateTime.UtcNow))),
        RunAsync(svc.AllPackagesAsync, rows => Fill(PackageChoices, rows.Where(p => p.IsActive).Select(p => new ItemChoice("package", p.Id, p.Name)))),
        RunAsync(svc.AllAmenitiesAsync, rows => Fill(AmenityChoices, rows.Where(a => a.IsActive).Select(a => new ItemChoice("amenity", a.Id, a.Name)))));

    /// <summary>What the form says the code fits; "all" wins over single ticks.</summary>
    public IReadOnlyList<DiscountItem> ChosenItems() =>
        (AllPackages ? [DiscountItem.Package(null)] : PackageChoices.Where(c => c.IsChosen).Select(c => DiscountItem.Package(c.Id)))
        .Concat(AllAmenities ? [DiscountItem.Amenity(null)] : AmenityChoices.Where(c => c.IsChosen).Select(c => DiscountItem.Amenity(c.Id)))
        .Concat(ForCoachAddon ? [DiscountItem.CoachAddon()] : [])
        .ToList();

    [RelayCommand]
    private async Task AddAsync()
    {
        Notice = null; ErrorMessage = null;
        var items = ChosenItems();
        if (items.Count == 0) { ErrorMessage = ErrorText.For(AppErrorKind.Validation, "no_items"); return; }
        // valid through the end of the chosen day, local time
        var d = new Discount { Code = NewCode.Trim().ToUpperInvariant(), Percent = NewPercent, MaxUses = NewLimit, ExpiresAt = ValidUntil.Date.AddDays(1).ToUniversalTime(), ShowOnPackages = NewShowToMembers };
        var fits = DiscountFits.Text(AllPackages, PackageChoices.Where(c => c.IsChosen).Select(c => c.Id), AllAmenities, AmenityChoices.Where(c => c.IsChosen).Select(c => c.Id),
            PackageChoices.ToDictionary(c => c.Id, c => c.Name), AmenityChoices.ToDictionary(c => c.Id, c => c.Name), ForCoachAddon);
        // G2: nothing can change afterwards, so the terms are read back before the code goes live
        if (!await Confirm.AskAsync($"Add code {d.Code}?",
                $"{d.Percent}% off, up to {d.MaxUses} uses, until {ValidUntil:d MMM yyyy}.\nFor: {fits}.\n"
                + (d.ShowOnPackages ? "Members see it." : "Hidden from members.") + "\nA code can't be changed after it's added.", "Add code")) return;
        if (!await RunAsync(() => svc.CreateDiscountAsync(d, items), _ => { })) return;
        Notice = $"Code {d.Code} is live." + (d.ShowOnPackages ? " Members can see it." : "");
        NewCode = ""; NewShowToMembers = true; AllPackages = AllAmenities = ForCoachAddon = false;
        await LoadAsync();
    }

    /// <summary>The Actions menu for one code, as on Users and roles: only what's allowed for it (G1).</summary>
    public IReadOnlyList<string> ActionsFor(DiscountRow row) =>
        new[] { row.Discount.Paused ? "Turn on" : "Turn off", CanArchive ? "Archive" : null, CanArchive && row.CanDelete ? "Delete" : null }.OfType<string>().ToList();

    [RelayCommand]
    private async Task RunActionAsync((DiscountRow Row, string Action) pick)
    {
        var (row, action) = pick; var d = row.Discount;
        Notice = null; ErrorMessage = null;
        switch (action)
        {
            case "Turn off":
                if (!await Confirm.AskAsync($"Turn off code {d.Code}?", "Nobody can use it, and members don't see it, until you turn it back on.", "Turn off")) return;
                if (!await RunAsync(() => svc.SetDiscountPausedAsync(d, true))) return;
                Notice = $"Code {d.Code} is off."; break;
            case "Turn on":
                if (!await Confirm.AskAsync($"Turn on code {d.Code}?", "It can be used again, and members see it if it's shown to them.", "Turn on")) return;
                if (!await RunAsync(() => svc.SetDiscountPausedAsync(d, false))) return;
                Notice = $"Code {d.Code} is on."; break;
            case "Archive":
                if (!await Confirm.AskAsync($"Archive code {d.Code}?", "It stops working and moves to the Archive. Payments made with it keep it.", "Archive")) return;
                if (!await RunAsync(() => svc.ArchiveDiscountAsync(d))) return;
                Notice = $"Code {d.Code} is archived."; break;
            case "Delete":
                if (!await Confirm.AskAsync($"Delete code {d.Code}?", "No payment has used it. It's removed for good.", "Delete")) return;
                if (!await RunAsync(() => svc.DeleteDiscountAsync(d))) return;
                Notice = $"Code {d.Code} is deleted."; break;
            default: return;
        }
        await LoadAsync();
    }
}

public partial class RevenueViewModel(IAdminService svc, IReportFiles? files = null) : LoadableViewModel
{
    public ObservableCollection<RevenueRow> Rows { get; } = new();
    public ObservableCollection<CashShift> Shifts { get; } = new();   // F2: drawer shifts opened in the same dates
    public ObservableCollection<CoachHoursRow> CoachHours { get; } = new();   // E11: completed hours per coach
    [ObservableProperty] private DateTime from = DateTime.Today.AddDays(-29);   // 30 days with today
    [ObservableProperty] private DateTime to = DateTime.Today;
    [ObservableProperty] private string totalText = "₱0.00";
    // what the screen shows; the PDF uses this, not dates edited since
    (DateOnly From, DateOnly To, decimal Total)? _loaded;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => LoadRangeAsync(DateOnly.FromDateTime(From), DateOnly.FromDateTime(To));

    async Task<bool> LoadRangeAsync(DateOnly from, DateOnly to)
    {
        var revTask = RunAsync(() => svc.RevenueAsync(from, to), rows =>
        {
            Fill(Rows, rows);
            TotalText = $"₱{rows.Sum(r => r.Total):N2}";
            _loaded = (from, to, rows.Sum(r => r.Total));
            ExportPdfCommand.NotifyCanExecuteChanged(); ExportShiftsPdfCommand.NotifyCanExecuteChanged(); ExportCoachHoursCsvCommand.NotifyCanExecuteChanged();
        });
        var shiftsTask = RunAsync(() => svc.CashShiftsAsync(from, to), shifts => { Fill(Shifts, shifts); OnPropertyChanged(nameof(ShiftTotalsText)); });
        var hoursTask = RunAsync(() => svc.CoachHoursAsync(from, to), hours => Fill(CoachHours, hours));
        var dashTask = LoadDashboardAsync(from, to);
        await Task.WhenAll(revTask, shiftsTask, hoursTask, dashTask);
        if (revTask.Result && dashTask.Result)
        {
            Fill(ByItem, RevenueDashboard.Shares(Rows.Select(r => (PayKind.Name(r.Kind), r.Total))));
            Fill(ByMethod, RevenueDashboard.Shares(Rows.Select(r => (PayMethod.Name(r.Method), r.Total))));
            _days = Chart.Reverse().Select(d =>
            {
                var that = Rows.Where(r => r.Day.Date == d.Day.Date).ToList();
                var caption = that.Count == 0 ? "No payments"
                    : string.Join(", ", that.Select(r => PayKind.Name(r.Kind)).Distinct()) + " · " + string.Join(", ", that.Select(r => PayMethod.Name(r.Method)).Distinct());
                return new RevenueDayRow(d.Day, d.Payments, d.Visits, d.Refunds, d.Net, caption);
            }).ToList();
            _allDays = false;
            ShowDays();
        }
        return revTask.Result && shiftsTask.Result && hoursTask.Result && dashTask.Result;
    }

    // ---------- the Windows page (owner 2026-11-12, mockup docs/mockups/revenue.html); Android shows only the parts above ----------
    public ObservableCollection<RevenueFigure> Figures { get; } = new();
    public ObservableCollection<RevenueShare> ByItem { get; } = new();
    public ObservableCollection<RevenueShare> ByMethod { get; } = new();
    public ObservableCollection<RevenueDayRow> DayRows { get; } = new();
    /// <summary>Each day of the period shown, oldest first, for the chart.</summary>
    [ObservableProperty] private IReadOnlyList<RevenueDay> chart = [];
    /// <summary>"revenue" or "visits": what the chart's columns show.</summary>
    [ObservableProperty] private string metric = "revenue";
    /// <summary>7, 30 or 90 when the period is that many days up to today; 0 for other dates.</summary>
    [ObservableProperty] private int rangeDays = 30;
    [ObservableProperty] private string chartTotalText = "";
    [ObservableProperty] private string chartSubText = "";
    [ObservableProperty] private string methodsFoot = "";
    [ObservableProperty] private string moreDaysText = "";
    [ObservableProperty] private bool hasMoreDays;
    bool _allDays;
    List<RevenueDayRow> _days = [];
    const int DaysShown = 7;

    [RelayCommand]
    private Task ShowRangeAsync(string days)
    {
        var n = int.Parse(days, System.Globalization.CultureInfo.InvariantCulture);
        To = DateTime.Today; From = DateTime.Today.AddDays(1 - n);
        return LoadAsync();
    }

    [RelayCommand]
    private void ShowMetric(string metric) { Metric = metric == "visits" ? "visits" : "revenue"; ChartTexts(); }

    [RelayCommand]
    private void ToggleAllDays() { _allDays = !_allDays; ShowDays(); }

    async Task<bool> LoadDashboardAsync(DateOnly from, DateOnly to)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var span = to.DayNumber - from.DayNumber + 1;
        RangeDays = to == today && span is 7 or 30 or 90 ? span : 0;
        var figTask = RunAsync(() => svc.RevenueDaysAsync(RevenueDashboard.FiguresFrom(today), today), days => Fill(Figures, RevenueDashboard.Figures(days, today)));
        var daysTask = RunAsync(() => svc.RevenueDaysAsync(from, to), days =>
        {
            Chart = days;
            ChartTexts();
            var payments = days.Sum(d => d.Payments); var refunds = days.Sum(d => d.Refunds); var gross = days.Sum(d => d.Net) + refunds;
            MethodsFoot = $"{payments:N0} {(payments == 1 ? "payment" : "payments")}"
                + (payments > 0 ? $" · {RevenueDashboard.Peso(gross / payments)} on average" : "")
                + (refunds > 0 ? $" · {RevenueDashboard.Peso(refunds)} refunded" : " · no refunds");
        });
        await Task.WhenAll(figTask, daysTask);
        return figTask.Result && daysTask.Result;
    }

    void ChartTexts()
    {
        var days = Math.Max(1, Chart.Count);
        var label = RangeDays == 90 ? "3 months" : $"{Chart.Count} {(Chart.Count == 1 ? "day" : "days")}";
        if (Metric == "visits")
        {
            var visits = Chart.Sum(d => d.Visits);
            ChartTotalText = $"{visits:N0} check-ins";
            ChartSubText = $"in {label} · {visits / (double)days:0.#} a day on average";
        }
        else
        {
            var net = Chart.Sum(d => d.Net);
            ChartTotalText = RevenueDashboard.Peso(net);
            ChartSubText = $"net in {label} · {RevenueDashboard.Peso(net / days)} a day on average";
        }
    }

    void ShowDays()
    {
        Fill(DayRows, _allDays ? _days : _days.Take(DaysShown).ToList());
        HasMoreDays = _days.Count > DaysShown;
        MoreDaysText = _allDays ? "Show the 7 latest days" : $"Show all {_days.Count} days";
    }

    public string ShiftTotalsText => $"Shortages: ₱{Shifts.Sum(s => s.Shortage):N2} · Overages: ₱{Shifts.Sum(s => s.Overage):N2}";

    // ---- D8 (2026-11-20): supervisor investigation notes on closed shifts ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAddingShiftNote), nameof(NoteShiftTitle))] private CashShift? noteShift;
    [ObservableProperty] private string supervisorNote = "";
    [ObservableProperty] private bool resolveShift;
    public bool IsAddingShiftNote => NoteShift is not null;
    public string NoteShiftTitle => NoteShift is { } s ? $"Add note for {s.Cashier}'s shift ({s.DifferenceText})" : "";

    [RelayCommand] private void StartShiftNote(CashShift shift) { NoteShift = shift; SupervisorNote = ""; ResolveShift = false; Notice = null; ErrorMessage = null; }
    [RelayCommand] private void CancelShiftNote() { NoteShift = null; SupervisorNote = ""; ResolveShift = false; }

    [RelayCommand]
    private async Task SaveShiftNoteAsync()
    {
        if (NoteShift is not { } s) return;
        if (string.IsNullOrWhiteSpace(SupervisorNote)) { ErrorMessage = "Type an investigation note first."; return; }
        Notice = null;
        if (!await RunAsync(() => svc.AddCashShiftNoteAsync(s.Id, SupervisorNote, ResolveShift), _ => { })) return;
        NoteShift = null; SupervisorNote = ""; ResolveShift = false;
        if (_loaded is { } shown) await RunAsync(() => svc.CashShiftsAsync(shown.From, shown.To), shifts => { Fill(Shifts, shifts); OnPropertyChanged(nameof(ShiftTotalsText)); });
        Notice = "Investigation note added.";
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportShiftsPdfAsync()
    {
        if (files is null || _loaded is not { } shown) return;
        Notice = null;
        IReadOnlyList<CashShift> shifts = [];
        if (!await RunAsync(() => svc.CashShiftsAsync(shown.From, shown.To), s => shifts = s)) return;   // fresh: an open shift's figures move
        Fill(Shifts, shifts);
        OnPropertyChanged(nameof(ShiftTotalsText));
        var name = $"cash-shifts-{shown.From:yyyy-MM-dd}-to-{shown.To:yyyy-MM-dd}.pdf";
        if (await RunAsync(() => Safe.RunAsync(async () => { await files.ShareAsync(name, CashShiftPdf.Build(shown.From, shown.To, shifts, DateTime.UtcNow, await files.FontAsync())); return true; })))
            Notice = "Cash shifts PDF created. Choose where to save or send it.";
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportCoachHoursCsvAsync()
    {
        if (files is null || _loaded is not { } shown) return;
        Notice = null;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Coach,Completed sessions,Completed hours,No-shows,Coach cancellations");
        foreach (var c in CoachHours)
            sb.AppendLine($"{AuditCsv.Cell(c.CoachName)},{c.CompletedSessions},{c.CompletedHours:0.##},{c.NoShows},{c.CoachCancelled}");
        var name = $"coach-hours-{shown.From:yyyy-MM-dd}-to-{shown.To:yyyy-MM-dd}.csv";
        if (await RunAsync(() => Safe.RunAsync(async () => { await files.ShareAsync(name, System.Text.Encoding.UTF8.GetBytes(sb.ToString())); return true; })))
            Notice = "Coach hours CSV created. Choose where to save or send it.";
    }

    bool CanExport() => files is not null && _loaded is not null;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportPdfAsync()
    {
        if (files is null || _loaded is not { } shown) return;
        Notice = null;
        IReadOnlyList<RevenueLine> lines = [];
        if (!await RunAsync(() => svc.RevenueLinesAsync(shown.From, shown.To), l => lines = l)) return;
        if (lines.Sum(l => l.Amount) != shown.Total)
        {
            await LoadRangeAsync(shown.From, shown.To);
            ErrorMessage = "Revenue changed since this report was loaded. The figures are now up to date; export again.";
            return;
        }
        var name = $"revenue-{shown.From:yyyy-MM-dd}-to-{shown.To:yyyy-MM-dd}.pdf";
        if (await RunAsync(() => Safe.RunAsync(async () => { await files.ShareAsync(name, RevenuePdf.Build(shown.From, shown.To, lines, DateTime.UtcNow, await files.FontAsync())); return true; })))
            Notice = "Revenue PDF created. Choose where to save or send it.";
    }
}

// admin: the gym's name, hours, the walk-in price, the drawer float and the booking down payment.
// Cashier plan Y8: no payment QR any more; members and the desk pay e-wallets through Xendit.
public partial class GymSettingsViewModel(IAdminService svc) : LoadableViewModel
{
    [ObservableProperty] private string gymName = "";
    [ObservableProperty] private decimal walkInPrice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasDayPassPackage), nameof(CanEditWalkInPrice), nameof(DayPassPriceText), nameof(DayPassPackageNote))]
    private MembershipPackage? dayPassPackage;
    public bool HasDayPassPackage => DayPassPackage is not null;
    public bool CanEditWalkInPrice => DayPassPackage is null;
    public string DayPassPriceText => DayPassPackage is { } p ? $"₱{p.Price:N2}" : "";
    public string DayPassPackageNote => DayPassPackage is { } p ? $"Set on the {p.Name} package. Change it under Packages and amenities." : "";
    [ObservableProperty] private TimeSpan opensAt = TimeSpan.FromHours(6);
    [ObservableProperty] private TimeSpan closesAt = TimeSpan.FromHours(22);
    [ObservableProperty] private decimal openingFloat = 5000;   // F2: what each cashier's drawer starts with
    [ObservableProperty] private int downPaymentPercent = 50;   // G13 (0064): of an amenity booking, paid to reserve it
    [ObservableProperty] private int maxCoachSessionsPerDay = 1;   // E12 (0119)
    [ObservableProperty] private int maxUpcomingCoachSessions = 2;   // E12 (0119)
    [ObservableProperty] private string maxCoachHoursPerDay = "6";   // E12 (0119): blank = unlimited
    GymSettings? _loaded;
    // UI test 2026-11-23: on a slow line the form showed default values (walk-in 0, down payment 50%) for seconds; it now waits
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool settingsLoaded;

    public override Task OnAppearingAsync() => Task.WhenAll(LoadAsync(), LoadClosuresAsync());

    [RelayCommand]
    private async Task LoadAsync()
    {
        var settingsTask = RunAsync(svc.GymSettingsAsync, g =>
        {
            _loaded = g; SettingsLoaded = true; GymName = g.GymName; WalkInPrice = g.WalkInPrice; OpensAt = g.OpensAt; ClosesAt = g.ClosesAt; OpeningFloat = g.OpeningFloat; DownPaymentPercent = g.DownPaymentPercent;
            MaxCoachSessionsPerDay = g.MaxCoachSessionsPerDay; MaxUpcomingCoachSessions = g.MaxUpcomingCoachSessions;
            MaxCoachHoursPerDay = g.MaxCoachHoursPerDay?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        });
        var pkgsTask = svc.AllPackagesAsync();
        await Task.WhenAll(settingsTask, pkgsTask);
        if (pkgsTask.Result.Ok)
            DayPassPackage = pkgsTask.Result.Value!.OrderBy(p => p.IncludesCoach)   // the plain Day pass is the walk-in price (UI test 2026-11-23)
                .FirstOrDefault(p => p.IsDayPass && p.IsActive && p.PublishedAt is not null);
    }

    [RelayCommand(CanExecute = nameof(SettingsLoaded))]
    private async Task SaveAsync()
    {
        Notice = null;
        if (_loaded is null) return;
        int? maxHours = null;
        if (!string.IsNullOrWhiteSpace(MaxCoachHoursPerDay))
        {
            if (!int.TryParse(MaxCoachHoursPerDay.Trim(), out var h) || h < 1) { ErrorMessage = "Coach daily hours must be at least 1, or leave blank."; return; }
            maxHours = h;
        }
        if (!await Confirm.AskAsync("Save gym settings?", $"Open {DateTime.Today.Add(OpensAt):h:mm tt} to {DateTime.Today.Add(ClosesAt):h:mm tt} every day.", "Save settings")) return;
        _loaded.GymName = GymName.Trim(); _loaded.WalkInPrice = WalkInPrice;
        _loaded.OpensAt = OpensAt; _loaded.ClosesAt = ClosesAt; _loaded.OpeningFloat = OpeningFloat; _loaded.DownPaymentPercent = DownPaymentPercent;
        _loaded.MaxCoachSessionsPerDay = MaxCoachSessionsPerDay; _loaded.MaxUpcomingCoachSessions = MaxUpcomingCoachSessions; _loaded.MaxCoachHoursPerDay = maxHours;
        if (!await RunAsync(() => svc.SaveGymSettingsAsync(_loaded))) return;
        Notice = "Saved.";
        await LoadAsync();
    }

    // ---- G16-G18 (0065): closed days. Whole Manila days, today or later, with a reason; reopening is allowed up to the day itself. ----
    static DateOnly ManilaToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
    public ObservableCollection<GymClosureRow> Closures { get; } = new();
    public bool HasClosures => Closures.Count > 0;
    public DateTime MinClosedDay => ManilaToday.ToDateTime(TimeOnly.MinValue);
    [ObservableProperty] private DateTime closedDay = DateTime.Today.AddDays(1);
    [ObservableProperty] private string closedReason = "";

    async Task LoadClosuresAsync()
    {
        await RunAsync(svc.ClosuresAsync, rows => Fill(Closures, rows.Select(c => new GymClosureRow(c.Day, c.Reason, c.Day >= ManilaToday))));
        OnPropertyChanged(nameof(HasClosures));
    }

    [RelayCommand]
    private async Task CloseDayAsync()
    {
        Notice = null; ErrorMessage = null;
        var day = DateOnly.FromDateTime(ClosedDay);
        if (string.IsNullOrWhiteSpace(ClosedReason)) { ErrorMessage = "Type why the gym is closed, for example Holiday."; return; }
        ClosurePreview? preview = null;
        if (!await RunAsync(() => svc.ClosurePreviewAsync(day), p => preview = p)) return;
        var when = day.ToString("dddd, d MMM", CultureInfo.InvariantCulture);
        if (!await Confirm.AskAsync($"Close the gym on {when}?", $"{preview!.Text}\nEveryone is told: \"The gym is closed on {when}: {ClosedReason.Trim().TrimEnd('.')}.\"", "Close the gym")) return;
        if (!await RunAsync(() => svc.CloseDayAsync(day, ClosedReason))) return;
        Notice = $"Closed on {when}. Everyone has been told."; ClosedReason = "";
        await LoadClosuresAsync();
    }

    [RelayCommand]
    private async Task ReopenAsync(GymClosureRow row)
    {
        Notice = null;
        if (!await Confirm.AskAsync($"Open the gym again on {row.DayText}?", "Everyone is told. Bookings, sessions and shifts that were cancelled stay cancelled.", "Open again")) return;
        if (!await RunAsync(() => svc.ReopenDayAsync(row.Day))) return;
        Notice = $"Open again on {row.DayText}.";
        await LoadClosuresAsync();
    }
}

/// <summary>A closed day on Gym settings; past ones stay as history and can't be reopened.</summary>
public sealed record GymClosureRow(DateOnly Day, string Reason, bool CanReopen)
{
    public string DayText => Day.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
}

public partial class AuditViewModel(IAdminService svc, ArchiveViewModel? archive = null) : LoadableViewModel
{
    public ArchiveViewModel? Archive { get; } = archive;
    public bool HasArchive => Archive is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowLogs), nameof(IsAuditTab), nameof(IsMoneyTab))] private bool showArchive;
    /// <summary>Plan 2026-11-17 Z6: the Transactions tab (payments, refunds, cash shifts); the audit log shows the rest.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAuditTab), nameof(IsMoneyTab))] private bool showMoney;
    public bool ShowLogs => !ShowArchive;
    public bool IsAuditTab => !ShowArchive && !ShowMoney;
    public bool IsMoneyTab => !ShowArchive && ShowMoney;
    public ObservableCollection<AuditRecord> Entries { get; } = new();

    // filters (D7): Manila dates, part of the user's name, action type, object type. Index 0 of each list is "All".
    public string[] Areas { get; } = ["All action types", .. AuditLabels.Areas.Values];
    public string[] ObjectTypes { get; } = ["All object types", .. AuditLabels.Objects.Values];
    [ObservableProperty] private int areaIndex;
    [ObservableProperty] private int objectIndex;
    [ObservableProperty] private string user = "";
    [ObservableProperty] private bool useDates;
    [ObservableProperty] private DateTime from = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime to = DateTime.Today;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText)), NotifyCanExecuteChangedFor(nameof(PreviousCommand))] private int page;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(NextCommand))] private bool hasMore;
    [ObservableProperty] private bool isEmpty;
    public string PageText => $"Page {Page + 1}";
    bool HasPrevious => Page > 0;
    AuditFilter _applied = new();
    // owner fixes 2026-11-19 Q4 (0101): Transactions has its own filters and its own applied filter; the two tabs never mix
    public string[] Whats { get; } = ["All", "Membership", "Amenity", "Day pass", "Coach add-on", "Refund", "Cash shift"];
    static readonly string[] WhatKeys = ["", "membership", "amenity", "day_pass", "coach_addon", "refund", "cash_shift"];
    public string[] PaidWiths { get; } = ["All", "Cash", "GCash", "Maya", "QR Ph", "Card"];
    static readonly string[] PaidWithKeys = ["", "cash", "gcash", "paymaya", "qrph", "cards"];   // Xendit's channel codes, or the payment's method
    public string[] Statuses { get; } = ["All", "Pending", "Paid", "Failed", "Refunded", "Voided"];
    static readonly string[] StatusKeys = ["", "pending", "paid", "failed", "refunded", "voided"];
    [ObservableProperty] private string customer = "";
    [ObservableProperty] private int whatIndex;
    [ObservableProperty] private int paidWithIndex;
    [ObservableProperty] private int statusIndex;
    [ObservableProperty] private string recordedBy = "";
    [ObservableProperty] private bool moneyUseDates;
    [ObservableProperty] private DateTime moneyFrom = DateTime.Today.AddDays(-7);
    [ObservableProperty] private DateTime moneyTo = DateTime.Today;
    AuditFilter _moneyApplied = new();
    AuditFilter Shown => ShowMoney ? _moneyApplied with { Money = true } : _applied with { Money = false };
    /// <summary>Set by the page: shows the Save dialog and writes the text. Returns where it went, or null if cancelled.</summary>
    public Func<string, string, Task<string?>>? SaveFile { get; set; }

    public override Task OnAppearingAsync() => ShowArchive && Archive is not null ? Archive.OnAppearingAsync() : LoadPageAsync(Page);

    AuditFilter Current() => ShowMoney
        ? new(MoneyUseDates ? DateOnly.FromDateTime(MoneyFrom) : null, MoneyUseDates ? DateOnly.FromDateTime(MoneyTo) : null, RecordedBy,
            Customer: Customer, What: WhatKeys[WhatIndex], PaidWith: PaidWithKeys[PaidWithIndex], Status: StatusKeys[StatusIndex])
        : new(UseDates ? DateOnly.FromDateTime(From) : null, UseDates ? DateOnly.FromDateTime(To) : null, User,
            AreaIndex > 0 ? AuditLabels.Areas.Keys.ElementAt(AreaIndex - 1) : null, ObjectIndex > 0 ? AuditLabels.Objects.Keys.ElementAt(ObjectIndex - 1) : null);

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy) return;
        if (ShowMoney ? MoneyUseDates && MoneyTo.Date < MoneyFrom.Date : UseDates && To.Date < From.Date) { ErrorMessage = "The end date must be on or after the start date."; return; }
        if (ShowMoney) _moneyApplied = Current(); else _applied = Current();
        await LoadPageAsync(0);
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        if (IsBusy) return;
        if (ShowMoney) { Customer = RecordedBy = ""; WhatIndex = PaidWithIndex = StatusIndex = 0; MoneyUseDates = false; MoneyFrom = DateTime.Today.AddDays(-7); MoneyTo = DateTime.Today; _moneyApplied = new(); }
        else { AreaIndex = ObjectIndex = 0; User = ""; UseDates = false; From = DateTime.Today.AddDays(-7); To = DateTime.Today; _applied = new(); }
        await LoadPageAsync(0);
    }

    async Task LoadPageAsync(int target)
    {
        IsEmpty = false;
        if (!await RunAsync(() => svc.AuditPageAsync(Shown, target), result =>
            { Fill(Entries, result.Rows); Page = target; HasMore = result.HasMore; IsEmpty = Entries.Count == 0; }))
        { Entries.Clear(); HasMore = false; }
    }

    [RelayCommand(CanExecute = nameof(HasPrevious))] private Task PreviousAsync() => !IsBusy && Page > 0 ? LoadPageAsync(Page - 1) : Task.CompletedTask;
    [RelayCommand(CanExecute = nameof(HasMore))] private Task NextAsync() => !IsBusy && HasMore ? LoadPageAsync(Page + 1) : Task.CompletedTask;

    /// <summary>Every row the applied filters match, not just this page, as CSV.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy || SaveFile is null) return;
        Notice = null;
        IReadOnlyList<AuditRecord>? rows = null;
        if (!await RunAsync(() => svc.AuditExportAsync(Shown), r => rows = r)) return;
        var where = ShowMoney ? await SaveFile($"transactions-{DateTime.Today:yyyy-MM-dd}.csv", TransactionsCsv.Build(rows!))
                              : await SaveFile($"audit-log-{DateTime.Today:yyyy-MM-dd}.csv", AuditCsv.Build(rows!));
        if (where is not null) Notice = $"Saved {rows!.Count} entr{(rows.Count == 1 ? "y" : "ies")} to {where}.";
    }

    [RelayCommand] private async Task OpenArchiveAsync()
    {
        if (Archive is null) return;
        ShowArchive = true;
        await Archive.OnAppearingAsync();
    }

    [RelayCommand] private async Task OpenLogsAsync() { var was = ShowMoney; ShowArchive = false; ShowMoney = false; await LoadPageAsync(was ? 0 : Page); }
    [RelayCommand] private async Task OpenMoneyAsync() { var was = ShowMoney; ShowArchive = false; ShowMoney = true; await LoadPageAsync(was ? Page : 0); }
}

public sealed record StatusChoice(string Value, string Label);

/// <summary>Front desk and admin: mark an amenity available, in use or unavailable, and see who was alerted.</summary>
public partial class AmenityStatusViewModel(IAmenityService svc, IReservationService? reservations = null, IPaymentStaffService? payments = null,
    DeskBookingViewModel? booking = null, IReportFiles? files = null, ISubscriptionService? refunds = null,
    ISessionService? sessions = null, IAttendanceService? members = null) : LoadableViewModel
{
    // ---- owner, 2026-11-17: the Booked card on the right: confirmed bookings not yet ended; View shows the details, Print opens the receipt PDF ----
    public ObservableCollection<BookedRow> Booked { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsViewing))] private BookedRow? viewing;
    public bool IsViewing => Viewing is not null;
    public bool HasBooked => Booked.Count > 0;

    public async Task LoadBookedAsync()
    {
        if (reservations is null) return;
        if (await reservations.BookedAsync() is { Ok: true, Value: { } rows })
            Sync(Booked, rows, r => r.Booking.Id, (a, b) => a.When == b.When && a.PaidText == b.PaidText && a.ReceiptText == b.ReceiptText && a.RefundText == b.RefundText);   // refreshed on a timer: no flicker
        OnPropertyChanged(nameof(HasBooked));
        if (Viewing is { } v) Viewing = Booked.FirstOrDefault(b => b.Booking.Id == v.Booking.Id);
    }
    void OnDeskBooked() => _ = LoadBookedAsync();
    [RelayCommand] private void View(BookedRow row) { Viewing = row; IsAskingRefund = false; }
    [RelayCommand] private void CloseView() { Viewing = null; IsAskingRefund = false; }

    // ---- 0096: the desk asks for a walk-in's refund with a reason; the System admin decides on Refunds ----
    [ObservableProperty] private bool isAskingRefund;
    [ObservableProperty] private string refundReason = "";
    [RelayCommand] private void AskRefund() { RefundReason = ""; IsAskingRefund = true; Notice = null; }
    [RelayCommand] private void KeepRefund() => IsAskingRefund = false;

    [RelayCommand]
    private async Task SendRefundAsync()
    {
        if (refunds is null || Viewing is not { CanRequestRefund: true, Booking.PaymentId: { } pay } row) return;
        if (string.IsNullOrWhiteSpace(RefundReason)) { ErrorMessage = "Say why the customer wants a refund."; return; }
        Notice = null;
        if (!await RunAsync(() => refunds.RequestWalkInRefundAsync(pay, RefundReason, row.IsUnder24Hours))) return;
        IsAskingRefund = false; Notice = "Refund requested. The System admin decides it on Refunds.";
        await LoadBookedAsync();
    }

    [RelayCommand]
    private async Task PrintReceiptAsync()
    {
        if (files is null || Viewing is not { } row) return;
        Notice = null;
        if (await RunAsync(() => Safe.RunAsync(async () => { await files.OpenAsync($"receipt-booking-{row.Booking.Id}.pdf", BookingReceiptPdf.Build(row, DateTime.UtcNow, await files.FontAsync())); return true; })))
            Notice = "Receipt opened. Print it from the PDF viewer (Ctrl+P).";
    }

    // ---- plan 2026-11-17 Z1, Z2: book a slot for a customer at the desk (its own view model, shown on demand) ----
    public DeskBookingViewModel? Booking { get; } = booking;
    public bool HasBooking => Booking is not null;
    [ObservableProperty] private bool isBooking;
    [RelayCommand]
    private async Task ToggleBookingAsync()
    {
        if (IsBooking || Booking is null) IsBooking = false;
        else await OpenBookingAsync();
    }
    async Task OpenBookingAsync()
    {
        IsBooking = true; IsBookingCoach = false;
        Booking!.Booked -= OnDeskBooked; Booking.Booked += OnDeskBooked;   // a new booking shows on the Booked card
        await Booking.OnAppearingAsync();
    }

    // ---- E5/E6/E7 (0120): Book a coach for a customer at the counter ----
    public bool HasCoachBooking => sessions is not null && members is not null;
    [ObservableProperty] private bool isBookingCoach;
    [ObservableProperty] private string coachCustomerSearch = "";
    [ObservableProperty] private MemberHit? coachCustomer;
    [ObservableProperty] private DateTime coachBookDay = DateTime.Today;
    [ObservableProperty] private int coachBookHours = 1;
    [ObservableProperty] private FreeCoachSlot? selectedDeskCoach;
    [ObservableProperty] private SessionsViewModel.HourOption? selectedDeskCoachHour;
    [ObservableProperty] private string deskCoachMethod = "Cash";
    [ObservableProperty] private string deskCoachCode = "";
    public ObservableCollection<MemberHit> CoachCustomerHits { get; } = new();
    public ObservableCollection<FreeCoachSlot> DeskFreeCoaches { get; } = new();
    public ObservableCollection<SessionsViewModel.HourOption> DeskCoachHours { get; } = new();

    partial void OnCoachCustomerSearchChanged(string value) => _ = SearchCoachCustomerAsync(value);
    partial void OnCoachBookDayChanged(DateTime value) { if (IsBookingCoach) _ = LoadDeskCoachesAsync(); }
    partial void OnSelectedDeskCoachChanged(FreeCoachSlot? value) => RefreshDeskCoachHours();

    async Task SearchCoachCustomerAsync(string q)
    {
        if (members is null || string.IsNullOrWhiteSpace(q)) { CoachCustomerHits.Clear(); return; }
        var res = await members.FindMembersAsync(q.Trim());
        if (res.Ok) Fill(CoachCustomerHits, res.Value!.Take(6).ToList());
    }

    void RefreshDeskCoachHours()
    {
        DeskCoachHours.Clear();
        if (SelectedDeskCoach is not { } c) { SelectedDeskCoachHour = null; return; }
        foreach (var h in c.FreeHours)
        {
            if (CoachBookHours == 2 && !c.FreeHours.Contains(h + 1)) continue;
            DeskCoachHours.Add(new SessionsViewModel.HourOption(h, $"{DateTime.Today.AddHours(h):h tt} – {DateTime.Today.AddHours(h + CoachBookHours):h tt}"));
        }
        SelectedDeskCoachHour = DeskCoachHours.FirstOrDefault();
    }

    [RelayCommand]
    private void SetDeskCoachHours(object? hours)
    {
        CoachBookHours = int.TryParse(hours?.ToString(), out var parsed) && parsed == 2 ? 2 : 1;
        RefreshDeskCoachHours();
    }

    [RelayCommand]
    private void PickDeskCoachMethod(string m) => DeskCoachMethod = m;

    [RelayCommand]
    private void ChooseCoachCustomer(MemberHit hit) { CoachCustomer = hit; CoachCustomerHits.Clear(); }

    [RelayCommand]
    private async Task ToggleCoachBookingAsync()
    {
        IsBookingCoach = !IsBookingCoach;
        if (IsBookingCoach) { IsBooking = false; await LoadDeskCoachesAsync(); }
    }

    async Task LoadDeskCoachesAsync()
    {
        if (sessions is null) return;
        var res = await sessions.FreeCoachesAsync(DateOnly.FromDateTime(CoachBookDay.Date));
        if (!res.Ok) { ErrorMessage = ErrorText.For(res.Error, res.Message); return; }
        Fill(DeskFreeCoaches, res.Value!);
        SelectedDeskCoach = DeskFreeCoaches.FirstOrDefault();
    }

    [RelayCommand]
    private async Task DeskBookCoachAsync()
    {
        ErrorMessage = Notice = null;
        if (sessions is null || CoachCustomer is not { } m || SelectedDeskCoach is not { } c || SelectedDeskCoachHour is not { } hr)
        {
            ErrorMessage = "Choose a member, coach, and available hour.";
            return;
        }
        var startUtc = new DateTimeOffset(CoachBookDay.Date.AddHours(hr.Hour), TimeSpan.FromHours(8)).UtcDateTime;
        var method = DeskCoachMethod == "Cash" ? "cash" : "qr";
        var res = await sessions.DeskBookCoachAsync(m.MemberId, c.CoachId, startUtc, CoachBookHours, method, string.IsNullOrWhiteSpace(DeskCoachCode) ? null : DeskCoachCode.Trim());
        if (!res.Ok)
        {
            ErrorMessage = ErrorText.For(res.Error, res.Message);
            if (res.Error == AppErrorKind.Conflict) await LoadDeskCoachesAsync();
            return;
        }
        var r = res.Value!;
        Notice = r.IsFree
            ? $"Booked {c.FullName} for {m.Username} on {CoachBookDay:ddd d MMM} at {hr.Label} (included)."
            : method == "cash"
                ? $"Booked {c.FullName} for {m.Username} at {hr.Label} — ₱{r.Amount:N2} cash recorded ({r.PaymentCode})."
                : $"Held {c.FullName} for {m.Username} for 15 minutes (code {r.PaymentCode}, ₱{r.Amount:N2}).";
        IsBookingCoach = false;
    }

    // ---- F13 booking refund claims ("I didn't use it"), decided by the desk with a reason; moved here from Payments to verify (cashier plan Y7) ----
    public ObservableCollection<ClaimRow> Claims { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDeciding), nameof(DecideTitle), nameof(DecideText))] private ClaimRow? deciding;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DecideTitle), nameof(DecideText))] private bool decideApprove;
    [ObservableProperty] private string decideReason = "";
    public bool IsDeciding => Deciding is not null;
    public bool HasClaims => Claims.Count > 0;
    public string DecideTitle => Deciding is null ? "" : $"{(DecideApprove ? "Approve" : "Decline")} {Deciding.Member}'s refund of {Deciding.Price}?";
    public string DecideText => DecideApprove ? "Approve refund" : "Decline";

    public async Task LoadClaimsAsync()
    {
        if (payments is null) return;
        await RunAsync(payments.ClaimsAsync, rows => Fill(Claims, rows));
        OnPropertyChanged(nameof(HasClaims));
    }

    [RelayCommand] private void ApproveClaim(ClaimRow row) { Notice = null; Deciding = row; DecideApprove = true; DecideReason = ""; }
    [RelayCommand] private void DeclineClaim(ClaimRow row) { Notice = null; Deciding = row; DecideApprove = false; DecideReason = ""; }
    [RelayCommand] private void KeepClaim() => Deciding = null;

    [RelayCommand]
    private async Task ConfirmDecisionAsync()
    {
        if (payments is null || Deciding is not { } row) return;
        if (!await RunAsync(() => payments.DecideClaimAsync(row.Claim.Id, DecideApprove, DecideReason))) return;
        Claims.Remove(row); OnPropertyChanged(nameof(HasClaims)); Deciding = null;
        Notice = DecideApprove ? $"Refund approved. Admin repays {row.Member} from Refunds." : $"Declined. {row.Member} is told why.";
    }

    // ---- G13 (0064): today's confirmed bookings with a balance; the desk takes it once, cash into the drawer or GCash with a reference ----
    public ObservableCollection<BalanceRow> Balances { get; } = new();
    public bool HasBalances => Balances.Count > 0;
    [ObservableProperty] private string balanceReference = "";

    public async Task LoadBalancesAsync(DateOnly manilaDay)
    {
        if (reservations is null) return;
        if (await reservations.BalancesAsync(manilaDay) is { Ok: true, Value: { } rows }) Fill(Balances, rows);
        OnPropertyChanged(nameof(HasBalances));
    }
    static DateOnly ManilaToday => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    [RelayCommand] private Task TakeBalance(BalanceRow row) => TakeAsync(row, PayMethod.Cash);
    [RelayCommand] private Task TakeBalanceGcash(BalanceRow row) => TakeAsync(row, PayMethod.Gcash);

    async Task TakeAsync(BalanceRow row, string method)
    {
        if (reservations is null) return;
        Notice = null;
        if (method == PayMethod.Gcash && string.IsNullOrWhiteSpace(BalanceReference)) { ErrorMessage = "Type the GCash reference first."; return; }
        if (!await Confirm.AskAsync($"Take {row.Member}'s balance?", $"{row.BalanceText} for {row.Amenity}, {row.When}, {(method == PayMethod.Cash ? "in cash into your drawer" : "by GCash")}.", "Take balance")) return;
        if (!await RunAsync(() => reservations.TakeBalanceAsync(row.Booking.Id, method, method == PayMethod.Gcash ? BalanceReference : null), _ => { })) return;
        BalanceReference = "";
        Notice = $"{row.Member}'s balance is paid.";
        Balances.Remove(row); OnPropertyChanged(nameof(HasBalances));
    }

    public ObservableCollection<AmenityCard> Cards { get; } = new();
    public StatusChoice[] Choices { get; } = [new("available", "Available"), new("in_use", "In use"), new("unavailable", "Unavailable")];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsEditing))] private AmenityCard? editing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDetail))] private StatusChoice choice = new("available", "Available");
    [ObservableProperty] private string reason = "";
    [ObservableProperty] private bool hasBackAt;
    [ObservableProperty] private DateTime backDate = DateTime.Today;
    [ObservableProperty] private TimeSpan backTime = TimeSpan.FromHours(12);

    public bool IsEditing => Editing is not null;
    public bool ShowDetail => Choice.Value == "unavailable";   // a reason and a return time only belong to "unavailable"

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        var boardTask = RunAsync(svc.BoardAsync, board => Fill(Cards, board.Cards));
        var balancesTask = LoadBalancesAsync(ManilaToday);
        var claimsTask = LoadClaimsAsync();
        var bookedTask = LoadBookedAsync();
        var resumeTask = Booking is { } b && !IsBooking ? b.ResumeWaitingAsync() : Task.FromResult(false);
        await Task.WhenAll(boardTask, balancesTask, claimsTask, bookedTask, resumeTask);
        if (resumeTask.Result) await OpenBookingAsync();   // a walk-in still paying online comes back on screen
    }

    // Quiet and in place: the error line, the notice and the open form stay as they are, and only cards that changed are swapped.
    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
        if (Booking is { } b) await b.PollWaitingAsync();
        await LoadBookedAsync();
        var board = await svc.BoardAsync();
        if (!board.Ok) return;
        Sync(Cards, board.Value!.Cards, c => c.Amenity.Id, (a, b) => a.SameAs(b));
    }

    [RelayCommand]
    private void Select(AmenityCard card)
    {
        Editing = card;
        ErrorMessage = null; Notice = null;
        Choice = Choices.First(c => c.Value == card.Availability);
        Reason = card.Reason ?? "";
        HasBackAt = card.BackAt is not null;
        var back = (card.BackAt ?? DateTime.UtcNow.AddHours(2)).ToLocalTime();
        BackDate = back.Date; BackTime = back.TimeOfDay;
    }

    [RelayCommand] private void QuickReason(string text) => Reason = text;

    [RelayCommand]
    private void Cancel() { Editing = null; ErrorMessage = null; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editing is not { } card) return;
        DateTime? back = null;
        if (ShowDetail && HasBackAt)
        {
            back = DateTime.SpecifyKind(BackDate.Date + BackTime, DateTimeKind.Local).ToUniversalTime();
            if (back <= DateTime.UtcNow) { ErrorMessage = "Pick a return time that is still ahead."; return; }
        }
        var reason = ShowDetail ? Reason.Trim() : "";

        Notice = null;
        AmenityChange? result = null;
        if (!await RunAsync(() => svc.SetStatusAsync(card.Amenity.Id, Choice.Value, reason, back), r => result = r)) return;   // the editor stays open with what was typed

        // the service judged "changed" against the amenity as it is now, not against this possibly older card
        Notice = result!.Changed ? Summary(card.Amenity.Name, Choice, result.Told) : $"Nothing changed for {card.Amenity.Name}.";
        Editing = null;
        await LoadAsync();
    }

    static string Summary(string name, StatusChoice choice, IReadOnlyList<NotifiedMember> told)
    {
        if (choice.Value == "in_use") return $"{name} is now in use. Members are not alerted for this.";
        var now = $"{name} is now {choice.Label.ToLowerInvariant()}.";
        if (told.Count == 0) return $"{now} Nobody had saved it, so no alerts were sent.";
        var names = string.Join(", ", told.Take(5).Select(t => t.Name)) + (told.Count > 5 ? $" and {told.Count - 5} more" : "");
        return $"{now} Alerted {told.Count} {(told.Count == 1 ? "member" : "members")}: {names}.";
    }
}

// F10 (0051): Admin and System admin write to members, coaches or both; it lands in their read-only Announcements conversation.
public partial class AnnouncementsViewModel(IAnnouncementService svc) : LoadableViewModel
{
    public ObservableCollection<AnnouncementRow> Posted { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CountText)), NotifyCanExecuteChangedFor(nameof(PostCommand))] private string body = "";
    [ObservableProperty] private string audience = "everyone";

    public string CountText => $"{Body.Trim().Length} / 2000";
    static string Words(string audience) => audience switch { "members" => "members", "coaches" => "coaches", _ => "members and coaches" };

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.ListAsync, rows => Fill(Posted, rows));
    [RelayCommand] private void PickAudience(string value) => Audience = value;

    bool CanPost() => Body.Trim().Length is > 0 and <= 2000;

    [RelayCommand(CanExecute = nameof(CanPost))]
    private async Task PostAsync()
    {
        Notice = null;
        if (!await Confirm.AskAsync("Post this announcement?", $"It goes to all {Words(Audience)}, with a notification. It can't be edited or removed afterwards.", "Post")) return;
        if (!await RunAsync(() => svc.PostAsync(Body, Audience))) return;
        Notice = $"Posted to all {Words(Audience)}.";
        Body = ""; Audience = "everyone";
        await LoadAsync();
    }
}

/// <summary>Plan 2026-11-17 Z1, Z2 (0087): on Amenity status the desk books a slot for a customer: find a member, or (0091, owner) book a
/// walk-in by name with no account; pick the amenity, day, time and length, and take the full price in cash into the open drawer.
/// The receipt shows on screen for a walk-in to photograph; a member also gets it in their Front desk chat.</summary>
public partial class DeskBookingViewModel(IAttendanceService members, IReservationService reservations, IPaymentStaffService? payments = null, IUrlOpener? opener = null) : LoadableViewModel
{
    public sealed record SlotOption(DateTime Utc, string Label);
    /// <summary>What the desk shows after booking: the customer photographs it (walk-in) or has it in chat (member).</summary>
    public sealed record ReceiptView(string ReceiptNo, string Customer, string Amenity, string When, string Paid, string Note);

    // ---- the customer: suggestions while typing or a walk-in; one at a time (owner, 2026-11-17; D1 2026-11-20) ----
    public ObservableCollection<MemberHit> Suggestions { get; } = new();
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool noMatch;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BookCommand), nameof(PayOnlineCommand))] private MemberHit? member;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BookCommand), nameof(PayOnlineCommand))] private bool isWalkIn;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BookCommand), nameof(PayOnlineCommand))] private string walkInName = "";
    [ObservableProperty] private ReceiptView? receipt;
    public bool HasSuggestions => Suggestions.Count > 0;

    readonly MemberSuggest _suggest = new(members);
    bool _suppressSearch;
    public TimeSpan SearchDelay { get => _suggest.Delay; set => _suggest.Delay = value; }
    partial void OnSearchChanged(string value) { if (!_suppressSearch) _ = SuggestAsync(immediate: false); }
    public Task SuggestNowAsync() => SuggestAsync(immediate: true);

    async Task SuggestAsync(bool immediate)
    {
        var (applied, rows, err) = await _suggest.RunAsync(Search, immediate);
        if (!applied) return;
        ErrorMessage = err;
        var hasQuery = (Search?.Trim().Length ?? 0) >= 2;
        if (hasQuery) { IsWalkIn = false; Member = null; }   // a new search drops the old pick (final review)
        Fill(Suggestions, rows.Take(8));
        NoMatch = hasQuery && err is null && rows.Count == 0;
        OnPropertyChanged(nameof(HasSuggestions));
    }

    [RelayCommand]
    private void ChooseSuggestion(MemberHit hit)
    {
        _suggest.Cancel();
        IsWalkIn = false;
        PickMember(hit);
        _suppressSearch = true;
        Search = hit.Username;
        _suppressSearch = false;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    [RelayCommand]
    private async Task PickTopSuggestionAsync()
    {
        if (Suggestions.Count == 0 && (Search?.Trim().Length ?? 0) >= 2) await SuggestAsync(immediate: true);
        if (Suggestions.Count > 0) ChooseSuggestion(Suggestions[0]);
    }

    [RelayCommand]
    private void PickMember(MemberHit hit)
    {
        Member = hit;
        _ = RefreshMemberStatusAsync(hit.MemberId);
    }

    async Task RefreshMemberStatusAsync(string memberId)
    {
        var res = await members.MemberStatusAsync(memberId);
        if (res is { Ok: true, Value: { } fresh } && Member?.MemberId == memberId) Member = fresh;
    }
    /// <summary>Book for a walk-in: hides suggestions (and the pick), shows the name box.</summary>
    [RelayCommand]
    private void StartWalkIn()
    {
        _suggest.Cancel();
        Notice = null; ErrorMessage = null; Member = null; NoMatch = false;
        Suggestions.Clear(); OnPropertyChanged(nameof(HasSuggestions));
        IsWalkIn = true;
    }

    // ---- the slot: the same grid and rules as Book an amenity, without the hour-ahead rule (they are at the counter) ----
    AmenityGrid? grid;
    public ObservableCollection<GridAmenity> Amenities { get; } = new();
    public ObservableCollection<SlotOption> Starts { get; } = new();
    public ObservableCollection<PickChip> Lengths { get; } = new();
    [ObservableProperty] private DateTime day = DateTime.Today;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BookCommand), nameof(PayOnlineCommand))] private GridAmenity? amenity;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BookCommand), nameof(PayOnlineCommand))] private SlotOption? start;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(BookCommand), nameof(PayOnlineCommand))] private PickChip? length;   // 0090: whole hours up to closing
    /// <summary>The last booking made here (the notice names it).</summary>
    public Reservation? LastBooked { get; private set; }
    /// <summary>Raised after a booking is made, so Amenity status refreshes its Booked card.</summary>
    public event Action? Booked;

    public override Task OnAppearingAsync() => LoadGridAsync();
    partial void OnDayChanged(DateTime value) => _ = LoadGridAsync();
    partial void OnAmenityChanged(GridAmenity? value) => FillStarts();
    partial void OnStartChanged(SlotOption? value)
    {
        Fill(Lengths, value is null || Amenity is null || grid is null ? []
            : BookingSlots.Lengths(grid, Amenity.Id, value.Utc, payFirst: false).Select(m => new PickChip(BookingSlots.LengthText(m), true, minutes: m)));
        Length = Lengths.FirstOrDefault();
    }

    async Task LoadGridAsync()
    {
        var keep = Amenity?.Id;
        await RunAsync(() => reservations.GridAsync(DateOnly.FromDateTime(Day)), g =>
        {
            grid = g; Fill(Amenities, g.Amenities);
            Amenity = Amenities.FirstOrDefault(a => a.Id == keep);
            FillStarts();
        });
    }

    void FillStarts()
    {
        var slots = new List<SlotOption>();
        if (grid is { } g && Amenity is { } a)
            foreach (var t in BookingSlots.HourStarts(g))
                if (BookingSlots.Lengths(g, a.Id, t, payFirst: false).Count > 0) slots.Add(new(t, t.AddHours(8).ToString("h:mm tt", CultureInfo.InvariantCulture)));
        Fill(Starts, slots);
        Start = null;
    }

    string? Customer => IsWalkIn ? (string.IsNullOrWhiteSpace(WalkInName) ? null : WalkInName.Trim()) : Member?.Username;
    bool CanBook() => Customer is not null && Amenity is not null && Start is not null && Length is not null && Waiting is null;
    bool CanPayOnline() => CanBook() && IsWalkIn && payments is not null;

    /// <summary>0092: a walk-in's online booking waiting for Xendit (shows Open checkout, Check payment and Cancel).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWaiting)), NotifyCanExecuteChangedFor(nameof(BookCommand), nameof(PayOnlineCommand))]
    private Reservation? waiting;
    public bool IsWaiting => Waiting is not null;

    /// <summary>Owner, 2026-11-18: reopening the page keeps watching a walk-in who is still paying.</summary>
    public async Task<bool> ResumeWaitingAsync()
    {
        if (Waiting is null && await reservations.MyWaitingWalkInAsync() is { Ok: true, Value: { } w }) Waiting = w;
        return IsWaiting;
    }

    bool polling;
    /// <summary>Owner, 2026-11-18: called on the page's timer, so nobody has to press Check payment.</summary>
    public async Task PollWaitingAsync()
    {
        if (polling || IsBusy || payments is null || Waiting is not { PaymentId: { } pay } w) return;
        polling = true;
        try
        {
            var state = await DeskOnlineWait.PollAsync(payments, pay);
            if (Waiting?.Id != w.Id) return;   // Cancel was pressed meanwhile
            if (state == OnlineWait.Paid) await PaidAsync();
            else if (state == OnlineWait.Over)
            {
                Waiting = null; Notice = $"{w.WalkInName}'s online payment expired or was cancelled, so the slot is not booked.";
                await LoadGridAsync();
            }
        }
        finally { polling = false; }
    }

    string WhenText(SlotOption s, PickChip len) => $"{s.Utc.AddHours(8):ddd d MMM}, {s.Label}, {len.Text}";

    [RelayCommand(CanExecute = nameof(CanBook))]
    private async Task BookAsync()
    {
        if (Customer is not { } who || Amenity is not { } a || Start is not { } s || Length is not { } len) return;
        Notice = null; Receipt = null;
        var when = WhenText(s, len);
        if (!await Confirm.AskAsync($"Book {a.Name} for {who}?", $"{when}.\nTake the full price in cash into your drawer.", "Book and take cash")) return;
        DeskReceipt? made = null;
        var memberId = IsWalkIn ? null : Member!.MemberId;
        if (!await RunAsync(() => reservations.DeskBookAsync(memberId, IsWalkIn ? who : null, a.Id, s.Utc, len.Minutes), r => made = r)) return;
        await DoneAsync(made!, who, a.Name, when, "in cash");
    }

    async Task DoneAsync(DeskReceipt made, string who, string amenity, string when, string how)
    {
        LastBooked = made.Booking;
        Receipt = new(made.ReceiptNo, who, amenity, when, $"₱{made.Booking.TotalPrice:N2} {how}",
            made.Booking.WalkInName is not null ? "No account: ask them to take a photo of this receipt. It is their reference at the desk."
                                                : "Also sent to their Front desk chat. That message is their reference.");
        if (IsWalkIn) WalkInName = "";
        Booked?.Invoke();
        await LoadGridAsync();
    }

    // 0092 (owner, 2026-11-17): a walk-in pays online. As on the Cashier (plan Y6): Xendit's checkout opens on this PC, the customer
    // scans its QR Ph (GCash, Maya or a bank app), then Check payment. The slot is only theirs once Xendit confirms.
    [RelayCommand(CanExecute = nameof(CanPayOnline))]
    private async Task PayOnlineAsync()
    {
        if (Customer is not { } who || Amenity is not { } a || Start is not { } s || Length is not { } len) return;
        Notice = null; Receipt = null;
        var when = WhenText(s, len);
        if (!await Confirm.AskAsync($"Book {a.Name} for {who}, paid online?", $"{when}.\nXendit's checkout opens on this PC for the customer to pay within 30 minutes.", "Pay online")) return;
        Reservation? started = null;
        if (!await RunAsync(() => reservations.DeskWalkInOnlineAsync(who, a.Id, s.Utc, len.Minutes), r => started = r)) return;
        Waiting = started;
        await OpenCheckoutAsync();
    }

    [RelayCommand]
    private async Task OpenCheckoutAsync()
    {
        if (payments is null || Waiting is not { PaymentId: { } pay }) return;
        OnlineCheckout? checkout = null;
        if (!await RunAsync(() => payments.PayOnlineAsync(pay), c => checkout = c)) return;
        if (checkout!.Paid) { await PaidAsync(); return; }
        if (checkout.Url is { } url && opener is not null) await opener.OpenAsync(url);
        Notice = "Xendit's checkout is open on this PC. Let the customer scan the QR Ph code with GCash, Maya or their bank app. This screen updates by itself once they have paid.";
    }

    [RelayCommand]
    private async Task CheckPaymentAsync()
    {
        if (payments is null || Waiting is not { PaymentId: { } pay }) return;
        var paid = false;
        if (!await RunAsync(() => payments.CheckOnlineAsync(pay), p => paid = p)) return;
        if (paid) await PaidAsync();
        else Notice = "Not paid yet. Wait for the customer to finish on Xendit's page, then check again.";
    }

    async Task PaidAsync()
    {
        if (Waiting is not { } w) return;
        DeskReceipt? now = null;
        if (!await RunAsync(() => reservations.DeskReceiptAsync(w.Id), r => now = r)) return;
        Waiting = null;
        if (now!.Booking.Status != "confirmed")
        {
            ErrorMessage = "Paid, but the slot was booked by someone else first. The System admin repays it from Refunds.";
            Booked?.Invoke(); await LoadGridAsync(); return;
        }
        var name = Amenities.FirstOrDefault(x => x.Id == w.AmenityId)?.Name ?? "Amenity";
        var when = $"{w.StartsAt.ToUniversalTime().AddHours(8):ddd d MMM, h:mm tt}, {BookingSlots.LengthText((int)(w.EndsAt - w.StartsAt).TotalMinutes)}";
        await DoneAsync(now, w.WalkInName ?? "", name, when, "online (Xendit)");
    }

    /// <summary>The customer walked away: the pending payment and its booking are cancelled. A late scan goes to staff as a refund.</summary>
    [RelayCommand]
    private async Task StopWaitingAsync()
    {
        if (payments is null || Waiting is not { PaymentId: { } pay }) return;
        if (!await RunAsync(() => payments.VerifyAsync(pay, false, "Customer left at the desk"))) return;
        Waiting = null; Notice = "Cancelled. Nothing was charged.";
    }
}

public partial class DamageReportsViewModel(IAdminService svc) : LoadableViewModel
{
    readonly List<DamageReportRow> all = new();
    public ObservableCollection<DamageReportRow> Reports { get; } = new();
    [ObservableProperty] private string filter = "open";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReviewing))] private DamageReportRow? reviewing;
    public bool IsReviewing => Reviewing is not null;
    [ObservableProperty] private string reviewNote = "";
    [ObservableProperty] private bool setMaintenance;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.DamageReportsAsync, rows => { all.Clear(); all.AddRange(rows); Apply(); });

    [RelayCommand] private void SetFilter(string f) { Filter = f; Apply(); }
    void Apply() => Fill(Reports, Filter == "all" ? all : all.Where(r => r.Report.Status == Filter).ToList());

    [RelayCommand]
    private void StartReview(DamageReportRow row)
    {
        Reviewing = row;
        ReviewNote = row.Report.ReviewNote ?? "";
        SetMaintenance = false;
    }

    [RelayCommand] private void CancelReview() { Reviewing = null; ReviewNote = ""; SetMaintenance = false; }

    [RelayCommand] private Task MarkReviewedAsync() => SubmitReviewAsync("reviewed");
    [RelayCommand] private Task CloseReportAsync() => SubmitReviewAsync("closed");

    async Task SubmitReviewAsync(string status)
    {
        if (Reviewing is not { } row) return;
        Notice = null;
        if (!await RunAsync(() => svc.ReviewDamageAsync(row.Report.Id, status, ReviewNote, SetMaintenance && row.HasAmenity))) return;
        Reviewing = null; ReviewNote = ""; SetMaintenance = false;
        Notice = status == "closed" ? "Damage report closed." : "Damage report marked reviewed.";
        await LoadAsync();
    }
}

/// <summary>Phase 18 (P1-P10): Admin Payroll page — Pay period and Salaries tabs, side drawer for lines, mark paid, PDF.</summary>
public sealed class PayrollLineItem(PayrollLine line, bool canRemove)
{
    public PayrollLine Line { get; } = line;
    public int LineId => Line.LineId;
    public string KindLabel => Line.Kind switch { "bonus" => "Bonus", "deduction" => "Deduction", "salary" => "Salary", _ => Line.Kind };
    public string? Note => Line.Note;
    public string AmountText => $"{(Line.Kind == "deduction" ? "-" : "+")}₱{Line.Amount:N2}";
    public bool CanRemove { get; } = canRemove && line.Kind != "salary";
}

public sealed partial class PayrollRowItem(PayrollRow row) : ObservableObject
{
    public PayrollRow Row { get; } = row;
    public string UserId => Row.UserId;
    public string FullName => Row.FullName;
    public string RoleLabel => Row.RoleLabel;
    public string HoursText => Row.HoursText;
    public bool NoSalary => Row.NoSalary;
    public decimal Salary => Row.Salary;
    public decimal Bonuses => Row.Bonuses;
    public decimal Deductions => Row.Deductions;
    public decimal Net => Row.Net;
    public string SalaryText => NoSalary ? "–" : $"₱{Salary:N2}";
    public string BonusesText => Bonuses == 0 ? "–" : $"₱{Bonuses:N2}";
    public string DeductionsText => Deductions == 0 ? "–" : $"₱{Deductions:N2}";
    public string NetText => NoSalary && Net == 0 ? "–" : $"₱{Net:N2}";
    [ObservableProperty] private bool isSelected;
}

public sealed partial class StaffSalaryItem(StaffSalary s, Func<StaffSalaryItem, Task> save) : ObservableObject
{
    public StaffSalary Data { get; } = s;
    public string UserId => Data.UserId;
    public string FullName => Data.FullName;
    public string RoleLabel => Data.RoleLabel;
    public bool NoSalary => Data.NoSalary;
    public string AmountText => Data.Amount is { } a ? $"₱{a:N2}" : "–";
    public string UpdatedText => Data.UpdatedAt is { } at ? $"{ManilaTime.Of(at):d MMM yyyy} by {Data.UpdatedByName}" : "–";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsNotEditing))] private bool isEditing;
    public bool IsNotEditing => !IsEditing;
    [ObservableProperty] private string amountInput = s.Amount?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    [ObservableProperty] private string? errorText;

    [RelayCommand]
    private void BeginEdit()
    {
        AmountInput = Data.Amount?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        ErrorText = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() { IsEditing = false; ErrorText = null; }

    [RelayCommand]
    private Task SaveAsync() => save(this);
}

public partial class PayrollViewModel : LoadableViewModel
{
    readonly IAdminService _svc;
    readonly IReportFiles? _files;
    string _gymName = "Slim and Shape";

    public PayrollViewModel(IAdminService svc, IReportFiles? files = null)
    {
        _svc = svc;
        _files = files;
        var today = ManilaToday();
        periodStartsOn = PeriodStartFor(today);
    }

    public static DateOnly ManilaToday() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
    public static DateOnly PeriodStartFor(DateOnly d) => d.Day <= 15 ? new DateOnly(d.Year, d.Month, 1) : new DateOnly(d.Year, d.Month, 16);
    public static DateOnly StepPrev(DateOnly s) => s.Day == 16 ? new DateOnly(s.Year, s.Month, 1) : new DateOnly(s.AddMonths(-1).Year, s.AddMonths(-1).Month, 16);
    public static DateOnly StepNext(DateOnly s) => s.Day == 1 ? new DateOnly(s.Year, s.Month, 16) : new DateOnly(s.AddMonths(1).Year, s.AddMonths(1).Month, 1);

    public ObservableCollection<PayrollRowItem> Rows { get; } = new();
    public ObservableCollection<PayrollLineItem> DrawerLines { get; } = new();
    public ObservableCollection<StaffSalaryItem> StaffSalaries { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPeriodTab), nameof(IsSalariesTab))]
    private string activeTab = "period";
    public bool IsPeriodTab => ActiveTab == "period";
    public bool IsSalariesTab => ActiveTab == "salaries";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PeriodLabel), nameof(CanMarkPaid))]
    private DateOnly periodStartsOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanMarkPaid))]
    private DateOnly periodEndsOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDraft), nameof(CanMarkPaid))]
    private bool isPaid;
    [ObservableProperty] private int periodId;
    [ObservableProperty] private string statusText = "Draft";
    [ObservableProperty] private string totalNetText = "₱0.00";
    [ObservableProperty] private string summaryText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNoSalary))]
    private int noSalaryCount;
    public bool HasNoSalary => NoSalaryCount > 0;
    public bool IsDraft => !IsPaid;
    public bool IsEmptyStaff => !IsBusy && Rows.Count == 0 && ErrorMessage is null;
    public bool CanMarkPaid => !IsPaid && Rows.Count > 0 && PeriodEndsOn <= ManilaToday();
    public string PeriodLabel => PayrollPdf.PeriodSpan(PeriodStartsOn, PeriodEndsOn == default ? PeriodStartFor(PeriodStartsOn).AddDays(14) : PeriodEndsOn);

    // Side drawer state
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDrawerOpen))]
    private PayrollRowItem? selectedRow;
    public bool IsDrawerOpen => SelectedRow is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBonusKind), nameof(IsDeductionKind))]
    private string lineKind = "bonus";
    public bool IsBonusKind => LineKind == "bonus";
    public bool IsDeductionKind => LineKind == "deduction";
    [ObservableProperty] private string lineAmountText = "";
    [ObservableProperty] private string lineNote = "";
    [ObservableProperty] private string? lineError;

    // Role-level fixed salaries (Salaries tab)
    [ObservableProperty] private string employeeRoleSalaryInput = "";
    [ObservableProperty] private string coachRoleSalaryInput = "";
    [ObservableProperty] private string employeeRoleSalaryStatus = "Not set yet";
    [ObservableProperty] private string coachRoleSalaryStatus = "Not set yet";
    [ObservableProperty] private string? roleSalaryError;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private void OpenPeriodTab() => ActiveTab = "period";
    [RelayCommand] private async Task OpenSalariesTabAsync() { ActiveTab = "salaries"; await LoadSalariesAsync(); }

    [RelayCommand]
    private Task PrevPeriodAsync()
    {
        PeriodStartsOn = StepPrev(PeriodStartsOn);
        CloseDrawer();
        return LoadPeriodAsync();
    }

    [RelayCommand]
    private Task NextPeriodAsync()
    {
        PeriodStartsOn = StepNext(PeriodStartsOn);
        CloseDrawer();
        return LoadPeriodAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var settingsTask = _svc.GymSettingsAsync();
        var periodTask = LoadPeriodAsync();
        var salariesTask = LoadSalariesAsync();
        await Task.WhenAll(settingsTask, periodTask, salariesTask);
        if (settingsTask.Result.Ok && settingsTask.Result.Value is { } gs && !string.IsNullOrWhiteSpace(gs.GymName))
            _gymName = gs.GymName;
    }

    async Task LoadPeriodAsync()
    {
        await RunAsync(() => _svc.PayrollPeriodAsync(PeriodStartsOn), ApplyPeriod);
        OnPropertyChanged(nameof(IsEmptyStaff));
        MarkPaidCommand.NotifyCanExecuteChanged();
        ExportPdfCommand.NotifyCanExecuteChanged();
    }

    async Task LoadSalariesAsync()
    {
        await RunAsync(() => _svc.PayrollRoleSalariesAsync(), roles =>
        {
            var emp = roles.FirstOrDefault(r => r.Role == "employee");
            var coach = roles.FirstOrDefault(r => r.Role == "coach");
            EmployeeRoleSalaryStatus = emp?.Amount is { } ea ? $"Current: ₱{ea:N2}" : "Not set yet";
            CoachRoleSalaryStatus = coach?.Amount is { } ca ? $"Current: ₱{ca:N2}" : "Not set yet";
            if (emp?.Amount is { } eVal && string.IsNullOrWhiteSpace(EmployeeRoleSalaryInput))
                EmployeeRoleSalaryInput = eVal.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            if (coach?.Amount is { } cVal && string.IsNullOrWhiteSpace(CoachRoleSalaryInput))
                CoachRoleSalaryInput = cVal.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        });
        await RunAsync(() => _svc.PayrollStaffAsync(), list =>
        {
            Fill(StaffSalaries, list.Select(s => new StaffSalaryItem(s, SaveSalaryAsync)));
        });
    }

    [RelayCommand]
    private Task SaveEmployeeRoleSalaryAsync() => SaveRoleSalaryAsync("employee", EmployeeRoleSalaryInput);

    [RelayCommand]
    private Task SaveCoachRoleSalaryAsync() => SaveRoleSalaryAsync("coach", CoachRoleSalaryInput);

    async Task SaveRoleSalaryAsync(string role, string input)
    {
        RoleSalaryError = null;
        if (!decimal.TryParse(input.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            RoleSalaryError = "Enter a salary above ₱0.";
            return;
        }
        var res = await _svc.SetRoleSalaryAsync(role, amount);
        if (!res.Ok) { RoleSalaryError = ErrorText.For(res.Error, res.Message); return; }
        var label = role == "coach" ? "Coach" : "Front desk";
        Notice = $"₱{amount:N2} applied to all {label} staff.";
        await LoadSalariesAsync();
        await LoadPeriodAsync();
    }

    void ApplyPeriod(PayrollPeriod p)
    {
        PeriodId = p.PeriodId;
        PeriodStartsOn = p.StartsOn;
        PeriodEndsOn = p.EndsOn;
        IsPaid = p.IsPaid;
        StatusText = p.IsPaid
            ? $"Paid {ManilaTime.Of(p.PaidAt!.Value).ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)} by {p.PaidByName ?? "Admin"}"
            : "Draft";
        TotalNetText = $"₱{p.TotalNet:N2}";
        NoSalaryCount = p.Rows.Count(r => r.NoSalary);
        SummaryText = p.Rows.Count == 0
            ? "No staff in this period"
            : NoSalaryCount > 0
                ? $"{p.Rows.Count} {(p.Rows.Count == 1 ? "person" : "people")} · {NoSalaryCount} with no salary set"
                : $"{p.Rows.Count} {(p.Rows.Count == 1 ? "person" : "people")}";

        var selectedId = SelectedRow?.UserId;
        Fill(Rows, p.Rows.Select(r => new PayrollRowItem(r) { IsSelected = r.UserId == selectedId }));
        if (selectedId is not null) SelectedRow = Rows.FirstOrDefault(r => r.UserId == selectedId);
        OnPropertyChanged(nameof(IsEmptyStaff));
        OnPropertyChanged(nameof(CanMarkPaid));
        MarkPaidCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task OpenDrawerAsync(PayrollRowItem? row)
    {
        if (row is null) return;
        foreach (var r in Rows) r.IsSelected = r.UserId == row.UserId;
        SelectedRow = row;
        LineError = null;
        LineAmountText = "";
        LineNote = "";
        LineKind = "bonus";
        await LoadDrawerLinesAsync();
    }

    [RelayCommand]
    private void CloseDrawer()
    {
        foreach (var r in Rows) r.IsSelected = false;
        SelectedRow = null;
        DrawerLines.Clear();
        LineError = null;
    }

    [RelayCommand] private void SetLineKind(string kind) { LineKind = kind == "deduction" ? "deduction" : "bonus"; LineError = null; }

    async Task LoadDrawerLinesAsync()
    {
        if (SelectedRow is not { } row || PeriodId <= 0) return;
        await RunAsync(() => _svc.PayrollLinesAsync(PeriodId, row.UserId), lines =>
            Fill(DrawerLines, lines.Where(l => l.Kind != "salary").Select(l => new PayrollLineItem(l, !IsPaid))));
    }

    [RelayCommand]
    private async Task AddLineAsync()
    {
        if (SelectedRow is not { } row || IsPaid) return;
        LineError = null;
        if (!decimal.TryParse(LineAmountText.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            LineError = "Enter an amount above ₱0.";
            return;
        }
        var note = LineNote.Trim();
        if (string.IsNullOrWhiteSpace(note) || note.Length > 200)
        {
            LineError = "Add a short note (1–200 characters).";
            return;
        }
        var res = await _svc.AddPayrollLineAsync(PeriodId, row.UserId, LineKind, amount, note);
        if (!res.Ok) { LineError = ErrorText.For(res.Error, res.Message); return; }
        LineAmountText = "";
        LineNote = "";
        await LoadPeriodAsync();
        await LoadDrawerLinesAsync();
    }

    [RelayCommand]
    private async Task RemoveLineAsync(PayrollLineItem? item)
    {
        if (item is null || !item.CanRemove) return;
        LineError = null;
        var res = await _svc.RemovePayrollLineAsync(item.LineId);
        if (!res.Ok) { LineError = ErrorText.For(res.Error, res.Message); return; }
        await LoadPeriodAsync();
        await LoadDrawerLinesAsync();
    }

    public string MarkPaidConfirmMessage()
    {
        var paidCount = Rows.Count(r => !r.NoSalary);
        var noSal = Rows.Where(r => r.NoSalary).Select(r => r.FullName).ToList();
        var msg = $"Mark {PeriodLabel} as paid? {paidCount} {(paidCount == 1 ? "person" : "people")}, {TotalNetText}.";
        if (noSal.Count > 0)
        {
            var names = noSal.Count == 1 ? noSal[0] : string.Join(", ", noSal[..^1]) + " and " + noSal[^1];
            msg += $" {names} {(noSal.Count == 1 ? "has" : "have")} no salary set and {(noSal.Count == 1 ? "gets" : "get")} nothing.";
        }
        return msg + " Paid periods can't be changed.";
    }

    [RelayCommand(CanExecute = nameof(CanMarkPaid))]
    private async Task MarkPaidAsync()
    {
        if (!CanMarkPaid) return;
        Notice = null;
        if (!await Confirm.AskAsync($"Mark {PeriodLabel} as paid?", MarkPaidConfirmMessage(), "Mark as paid")) return;
        var res = await _svc.MarkPayrollPaidAsync(PeriodId);
        if (!res.Ok)
        {
            var msg = res.Message ?? "";
            if (msg.StartsWith("net_below_zero:", StringComparison.Ordinal))
            {
                var who = msg["net_below_zero:".Length..];
                var target = Rows.FirstOrDefault(r => r.FullName == who);
                if (target is not null) await OpenDrawerAsync(target);
                LineError = ErrorText.For(res.Error, res.Message);
            }
            else
            {
                ErrorMessage = ErrorText.For(res.Error, res.Message);
            }
            return;
        }
        ApplyPeriod(res.Value!);
        if (SelectedRow is not null) await LoadDrawerLinesAsync();
        Notice = $"{PeriodLabel} marked as paid.";
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (_files is null) return;
        Notice = null;
        PayrollPeriod? current = null;
        if (!await RunAsync(() => _svc.PayrollPeriodAsync(PeriodStartsOn), p => current = p) || current is null) return;
        var linesByUser = new Dictionary<string, IReadOnlyList<PayrollLine>>();
        foreach (var r in current.Rows)
        {
            var res = await _svc.PayrollLinesAsync(current.PeriodId, r.UserId);
            if (res.Ok && res.Value is not null) linesByUser[r.UserId] = res.Value;
        }
        var name = $"payroll-{current.StartsOn:yyyy-MM-dd}-to-{current.EndsOn:yyyy-MM-dd}.pdf";
        if (await RunAsync(() => Safe.RunAsync(async () => { await _files.ShareAsync(name, PayrollPdf.Build(_gymName, current, linesByUser, DateTime.UtcNow, await _files.FontAsync())); return true; })))
            Notice = "Payroll PDF created. Choose where to save or send it.";
    }

    async Task SaveSalaryAsync(StaffSalaryItem item)
    {
        item.ErrorText = null;
        if (!decimal.TryParse(item.AmountInput.Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            item.ErrorText = "Enter a salary above ₱0.";
            return;
        }
        var res = await _svc.SetStaffSalaryAsync(item.UserId, amount);
        if (!res.Ok) { item.ErrorText = ErrorText.For(res.Error, res.Message); return; }
        item.IsEditing = false;
        Notice = $"Salary saved for {item.FullName}.";
        await LoadSalariesAsync();
        await LoadPeriodAsync();
    }
}
