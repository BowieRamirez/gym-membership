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

public partial class HomeViewModel(IDeskHomeService svc) : LoadableViewModel
{
    public ObservableCollection<HomeTile> Tiles { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.SummaryAsync, s => Show(s));

    // Quiet: the error line is never touched and a failed look leaves the numbers as they were.
    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
        var result = await svc.SummaryAsync();
        if (!result.Ok) return;
        ErrorMessage = null;   // a look that works replaces the banner a failed first load left above these numbers
        Show(result.Value!);
    }

    void Show(DeskSummary s) => Sync(Tiles, Build(s), t => t.Route, (a, b) => a == b);

    public static IReadOnlyList<HomeTile> Build(DeskSummary s)
    {
        var amenityDetail = string.Join(", ", new[] { s.AmenitiesInUse > 0 ? $"{s.AmenitiesInUse} in use" : null, s.AmenitiesUnavailable > 0 ? $"{s.AmenitiesUnavailable} unavailable" : null }.OfType<string>());
        return
        [
            new("checkin", "log-in", "Check-ins today", s.CheckInsToday.ToString(),
                s.CheckInsToday == 0 ? "No one has checked in yet" : "Tap to check someone in", false),
            new("verify", "receipt", "Payments to verify", s.PaymentsToVerify.ToString(),
                s.PaymentsToVerify == 0 ? "All caught up" : s.PaymentsToVerify == 1 ? "1 payment is waiting" : $"{s.PaymentsToVerify} payments are waiting", s.PaymentsToVerify > 0),
            new("amenities", "waves", "Amenities available", $"{s.AmenitiesAvailable} of {s.AmenitiesTotal}",
                amenityDetail.Length == 0 ? "All open" : amenityDetail, s.AmenitiesUnavailable > 0),
            new("messages", "message-square", "Unread messages", s.UnreadMessages.ToString(),
                s.UnclaimedChats > 0 ? (s.UnclaimedChats == 1 ? "1 chat nobody has taken" : $"{s.UnclaimedChats} chats nobody has taken")
                : s.UnreadMessages > 0 ? "Open Messages to reply" : "No new messages", s.UnreadMessages > 0 || s.UnclaimedChats > 0),
        ];
    }
}

public partial class VerifyPaymentsViewModel(IPaymentStaffService svc) : LoadableViewModel
{
    public ObservableCollection<PaymentRow> Pending { get; } = new();
    [ObservableProperty] private PaymentRow? rejecting;
    [ObservableProperty] private string rejectReason = "";
    [ObservableProperty] private PaymentRow? viewing;
    [ObservableProperty] private string? proofUrl;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.PendingAsync, rows => Fill(Pending, rows));

    [RelayCommand]
    private async Task ApproveAsync(PaymentRow row)
    {
        Notice = null;
        if (!await Confirm.AskAsync($"Approve {row.Payer}'s payment?", $"{row.Item}\n₱{row.Payment.Amount:N2}", "Approve")) return;
        if (await RunAsync(() => svc.VerifyAsync(row.Payment.Id, true)))
        { Pending.Remove(row); Notice = $"Verified {row.Payer}'s payment. Receipt number assigned."; if (Viewing == row) Viewing = null; }
    }

    // a rejection needs a reason, and it is final: the member pays again with a new payment
    [RelayCommand] private void Reject(PaymentRow row) { Rejecting = row; RejectReason = ""; Notice = null; }
    [RelayCommand] private void KeepPending() => Rejecting = null;

    [RelayCommand]
    private async Task ConfirmRejectAsync()
    {
        if (Rejecting is not { } row) return;
        if (!await RunAsync(() => svc.VerifyAsync(row.Payment.Id, false, RejectReason))) return;
        Pending.Remove(row); Rejecting = null;
        Notice = $"Rejected {row.Payer}'s payment. They can pay again.";
    }

    // the screenshot the member sent, to check against the gym's e-wallet
    [RelayCommand]
    private async Task ViewProofAsync(PaymentRow row)
    {
        Viewing = row; ProofUrl = null;
        if (row.Payment.ProofPath is { } path) await RunAsync(() => svc.ProofUrlAsync(path), url => ProofUrl = url);
    }

    [RelayCommand] private void CloseProof() { Viewing = null; ProofUrl = null; }
}

/// <summary>One thing for sale on the cashier's grid, and whether it is the one picked.</summary>
public partial class ItemOption(CounterItem item) : ObservableObject
{
    public CounterItem Item { get; } = item;
    public string Name => Item.Label;
    public string PriceText => Item.Price is { } p ? $"₱{p:N2}" : "";
    [ObservableProperty] private bool isPicked;
}

// front desk: cash, GCash and bank transfers at the counter, and walk-ins. Verified straight away with a receipt number
public partial class CashierViewModel(IPaymentStaffService svc, IAttendanceService members) : LoadableViewModel
{
    public string[] Kinds { get; } = ["Membership", "Renewal", "Amenity", "Walk-in"];
    public string[] Methods { get; } = ["Cash", "GCash", "Bank transfer"];
    public ObservableCollection<MemberHit> Hits { get; } = new();
    public ObservableCollection<ItemOption> Options { get; } = new();
    public ObservableCollection<CashRow> CashToday { get; } = new();
    int itemsTicket;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWalkIn), nameof(NeedsMember), nameof(NeedsReference), nameof(OrderWho), nameof(OrderWhat), nameof(OrderHow), nameof(TotalText), nameof(TotalNote), nameof(ItemsEmptyText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))]
    private string kind = "Membership";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsReference), nameof(OrderHow)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string method = "Cash";
    [ObservableProperty] private string search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWho), nameof(ItemsEmptyText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private MemberHit? member;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWhat), nameof(TotalText)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private CounterItem? item;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(OrderWho)), NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string walkInName = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RecordCommand))] private string referenceNo = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TotalNote))] private string discountCode = "";
    [ObservableProperty] private string cashTotal = "₱0.00";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ItemsEmptyText))] private bool loadingItems;

    public bool IsWalkIn => Kind == "Walk-in";
    public bool NeedsMember => !IsWalkIn;
    public bool NeedsReference => Method != "Cash";

    // the order panel, in the words the cashier uses
    public string OrderWho => IsWalkIn ? (string.IsNullOrWhiteSpace(WalkInName) ? "Walk-in: type a name" : WalkInName.Trim()) : Member?.Username ?? "Find and choose a member";
    public string OrderWhat => Item?.Label ?? (IsWalkIn ? "Day pass" : "Choose what they are paying for");
    public string TotalText => IsWalkIn ? "Day pass" : Item?.Price is { } p ? $"₱{p:N2}" : "₱0.00";
    public string TotalNote => IsWalkIn ? "The day pass price is set in Gym settings."
        : string.IsNullOrWhiteSpace(DiscountCode) ? "" : "The discount code is checked when you complete the payment.";
    public string ItemsEmptyText => LoadingItems ? "Loading…"
        : Kind == "Renewal" && Member is null ? "Choose a member to see their memberships." : "Nothing to sell here yet.";
    /// <summary>The kind and the way of paying in words, so the order reads the same to someone who cannot see which button is filled.</summary>
    public string OrderHow => $"{Kind}, paid with {Method}";

    static string KindKey(string k) => k switch { "Renewal" => PayKind.Renewal, "Amenity" => PayKind.Amenity, "Walk-in" => PayKind.WalkIn, _ => PayKind.Membership };
    static string MethodKey(string m) => m switch { "GCash" => PayMethod.Gcash, "Bank transfer" => PayMethod.BankTransfer, _ => PayMethod.Cash };

    public override async Task OnAppearingAsync() { await RefreshItemsAsync(); await LoadCashAsync(); }

    // an item belongs to the kind (and member) it was listed for: change either and the pick goes
    partial void OnKindChanged(string value) { Item = null; _ = RefreshItemsAsync(); }
    partial void OnMemberChanged(MemberHit? value) { Item = null; if (Kind == "Renewal") _ = RefreshItemsAsync(); }
    partial void OnItemChanged(CounterItem? value) { foreach (var o in Options) o.IsPicked = value is not null && o.Item.Id == value.Id; }

    async Task RefreshItemsAsync()
    {
        var ticket = ++itemsTicket;   // the newest request wins: a slow answer for a kind you have left is dropped
        // Cards listed for another kind or member must not stay tappable while the right ones load: a tap would send their item id with the new kind.
        Options.Clear(); Item = null; ErrorMessage = null;
        if (IsWalkIn) { LoadingItems = false; return; }
        LoadingItems = true;
        var result = await svc.ItemsAsync(KindKey(Kind), Member?.MemberId);
        if (ticket != itemsTicket) return;   // a newer request owns the screen now; a stale answer, even a failed one, says nothing
        LoadingItems = false;
        if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
        Fill(Options, result.Value!.Select(r => new ItemOption(r)));
    }

    async Task LoadCashAsync() =>
        await RunAsync(() => svc.CashSummaryAsync(DateOnly.FromDateTime(DateTime.Today)), rows =>
        { Fill(CashToday, rows); CashTotal = $"₱{rows.Sum(r => r.Total):N2}"; });

    [RelayCommand] private Task FindAsync() => RunAsync(() => members.FindMembersAsync(Search), rows => Fill(Hits, rows));
    [RelayCommand] private void PickKind(string value) => Kind = value;
    [RelayCommand] private void PickMethod(string value) => Method = value;
    [RelayCommand] private void PickItem(ItemOption option) => Item = option.Item;   // OnItemChanged marks the card

    bool CanComplete() =>
        (IsWalkIn ? !string.IsNullOrWhiteSpace(WalkInName) : Member is not null && Item is not null)
        && (!NeedsReference || !string.IsNullOrWhiteSpace(ReferenceNo));

    // The command is off while it runs, so a double tap records one payment.
    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task RecordAsync()
    {
        Notice = null;
        if (!IsWalkIn && (Member is null || Item is null)) { ErrorMessage = "Choose the member and what they are paying for."; return; }
        if (!await Confirm.AskAsync("Record this payment?", $"{OrderWho}\n{OrderWhat}\n{TotalText}\n{OrderHow}", "Record payment")) return;
        Payment? paid = null;
        if (!await RunAsync(() => svc.RecordCounterAsync(KindKey(Kind), MethodKey(Method), IsWalkIn ? null : Member!.MemberId, Item?.Id,
                IsWalkIn ? WalkInName : null, NeedsReference ? ReferenceNo : null, IsWalkIn ? null : DiscountCode), p => paid = p)) return;
        Notice = $"Payment complete: ₱{paid!.Amount:N2}. Receipt {paid.ReceiptNo}.";
        Item = null; ReferenceNo = ""; DiscountCode = ""; WalkInName = "";
        await LoadCashAsync();
    }
}

// admin: verified payments, newest first, so a mistake can be voided with a reason and then recorded again
public partial class VoidPaymentsViewModel(IPaymentStaffService svc) : LoadableViewModel
{
    public ObservableCollection<PaymentRow> Recent { get; } = new();
    [ObservableProperty] private PaymentRow? voiding;
    [ObservableProperty] private string reason = "";

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.RecentAsync, rows => Fill(Recent, rows));
    [RelayCommand] private void Start(PaymentRow row) { Voiding = row; Reason = ""; Notice = null; }
    [RelayCommand] private void Keep() => Voiding = null;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Voiding is not { } row) return;
        if (!await RunAsync(() => svc.VoidAsync(row.Payment.Id, Reason))) return;
        Voiding = null;
        Notice = $"Voided {row.Payer}'s payment. What it switched on is reversed.";
        await LoadAsync();
    }
}

public partial class VerifyAmenityUsagesViewModel(IAmenityService svc) : LoadableViewModel
{
    public ObservableCollection<UsageRow> Pending { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.PendingUsagesAsync, rows => Fill(Pending, rows));
    [RelayCommand] private Task ApproveAsync(UsageRow row) => DecideAsync(row, true);
    [RelayCommand] private Task RejectAsync(UsageRow row) => DecideAsync(row, false);

    async Task DecideAsync(UsageRow row, bool approve)
    { if (await RunAsync(() => svc.VerifyUsageAsync(row.Usage.Id, approve))) Pending.Remove(row); }
}

public partial class CheckInViewModel(IAttendanceService svc, ICoachService coaches) : LoadableViewModel
{
    public ObservableCollection<MemberHit> Hits { get; } = new();
    public ObservableCollection<CoachPresenceRow> Coaches { get; } = new();
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string scanInput = "";   // a QR scanner device types the code here and presses Enter
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasScan))] private MemberScan? scan;
    [ObservableProperty] private bool isScanning;          // the camera is on
    public bool HasScan => Scan is not null;

    public override async Task OnAppearingAsync() { await SearchAsync(); await LoadCoachesAsync(); }

    [RelayCommand] private void ToggleCamera() => IsScanning = !IsScanning;

    /// <summary>D12: shows who the code belongs to and their live status. Never checks in by itself.</summary>
    [RelayCommand]
    private async Task ScanAsync(string? code)
    {
        code = (code ?? ScanInput).Trim();
        if (code.Length == 0) return;
        Notice = null; IsScanning = false; ScanInput = "";
        await RunAsync(() => svc.ScanAsync(code), s => Scan = s);
    }

    [RelayCommand]
    private async Task CheckInScannedAsync()
    {
        if (Scan is not { CanCheckIn: true, Member: { } id } s) return;
        Notice = null;
        if (await RunAsync(() => svc.CheckInAsync(id))) { Notice = $"{s.Name} is checked in."; Scan = null; }
    }

    [RelayCommand] private void ClearScan() => Scan = null;

    [RelayCommand] private Task SearchAsync() => RunAsync(() => svc.FindMembersAsync(Search), rows => Fill(Hits, rows));

    Task LoadCoachesAsync() => RunAsync(coaches.PresenceAsync, rows => Fill(Coaches, rows));

    [RelayCommand]
    private async Task CheckInAsync(MemberHit hit)
    {
        Notice = null;
        if (await RunAsync(() => svc.CheckInAsync(hit.MemberId))) Notice = $"{hit.Username} is checked in.";
    }

    [RelayCommand]
    private async Task ToggleCoachAsync(CoachPresenceRow row)
    {
        Notice = null;
        if (!await (row.InGym ? Confirm.AskAsync($"Check {row.Name} out?", "Members will no longer see them as in the gym.", "Check out")
                              : Confirm.AskAsync($"Check {row.Name} in?", "Members will see they are here and can hire them.", "Check in"))) return;
        var ok = await RunAsync(() => row.InGym ? coaches.CheckOutAsync(row.CoachId) : coaches.CheckInAsync(row.CoachId));
        if (!ok) return;
        Notice = row.InGym ? $"{row.Name} is checked out." : $"{row.Name} is checked in.";
        await LoadCoachesAsync();
    }
}

// ---------- coach ----------
public partial class CoachProfileViewModel(ICoachService svc, CoachPhotoViewModel? photo = null) : LoadableViewModel
{
    public CoachPhotoViewModel? Photo { get; } = photo;
    [ObservableProperty] private string? bio;
    [ObservableProperty] private string? specialty;
    [ObservableProperty] private string rateInput = "";   // text, parsed by CoachRate: never by the device's own decimal separator
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HiringText))] private bool isAvailable;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InGym), nameof(VisitText), nameof(VisitButton))] private CoachVisit? visit;

    public bool InGym => Visit is not null;
    public string VisitText => Visit is { } v ? $"You are checked in since {Fmt.Time(v.CheckedInAt)}. Members can find you and hire you." : "You are not checked in. Members won't see you in the coach list until you are.";
    public string VisitButton => InGym ? "Check out" : "Check in";
    public string HiringText => IsAvailable ? "On" : "Off";

    public override async Task OnAppearingAsync()
    {
        await LoadAsync();
        if (Photo is not null) await Photo.OnAppearingAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (await RunAsync(svc.MyCoachAsync, c =>
            { if (c is null) return; Bio = c.Bio; Specialty = c.Specialty; RateInput = CoachRate.Input(c.HourlyRate); IsAvailable = c.IsAvailable; }))
            await RunAsync(svc.MyVisitAsync, v => Visit = v);
    }

    [RelayCommand]
    private async Task ToggleVisitAsync()
    {
        Notice = null;
        var wasIn = InGym;
        if (!await (wasIn ? Confirm.AskAsync("Check out of the gym?", "Members will no longer see you as in the gym.", "Check out")
                          : Confirm.AskAsync("Check in to the gym?", "Members will see you are here and can hire you.", "Check in"))) return;
        if (!await RunAsync(() => wasIn ? svc.CheckOutAsync() : svc.CheckInAsync())) return;
        await RunAsync(svc.MyVisitAsync, v => Visit = v);
        Notice = wasIn ? "You are checked out." : "You are checked in.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Notice = null;
        // D10: a rate is required on every profile save; a bad one keeps what was typed and says how to fix it
        if (!CoachRate.TryParse(RateInput, out var rate)) { ErrorMessage = ErrorText.For(AppErrorKind.Validation, "bad_rate"); return; }
        if (await RunAsync(() => svc.UpdateProfileAsync(Bio, Specialty, rate, IsAvailable))) { RateInput = CoachRate.Input(rate); Notice = "Profile saved."; }
    }
}

public partial class TraineesViewModel(ICoachService svc, IChatService messages, IAppNavigator nav) : LoadableViewModel
{
    public ObservableCollection<TraineeRow> Trainees { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.TraineesAsync, rows => Fill(Trainees, rows));

    [RelayCommand]
    private void Message(TraineeRow row) { messages.Pending = (row.CoachId, row.MemberId); nav.GoToMessages(); }

    [RelayCommand]
    private async Task RemoveAsync(TraineeRow row)
    {
        Notice = null;
        if (!await Confirm.AskAsync($"Remove {row.Name}?", "They will no longer be your trainee.", "Remove trainee")) return;
        if (await RunAsync(() => svc.EndHireAsync(row.HireId))) { Notice = $"{row.Name} is no longer your trainee."; await LoadAsync(); }
    }
}

public partial class ShiftsViewModel(ICoachService svc) : LoadableViewModel
{
    public ObservableCollection<ShiftPost> Shifts { get; } = new();
    public int[] HoursOptions { get; } = [2, 4, 6, 8, 10];

    [ObservableProperty] private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan time = new(8, 0, 0);
    [ObservableProperty] private int hours = 4;   // D10 default length
    [ObservableProperty] private string? note;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HiringButtonText))] private bool isOpen = true;

    public string HiringButtonText => IsOpen ? "Pause hiring" : "Resume hiring";

    public override async Task OnAppearingAsync()
    {
        await LoadAsync();
        await RunAsync(svc.MyCoachAsync, c => { if (c is not null) IsOpen = c.IsAvailable; });
    }

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.MyShiftsAsync, rows => Fill(Shifts, rows));

    /// <summary>Flips the coach's availability: while paused, members see the coach grayed out and cannot hire.</summary>
    [RelayCommand]
    private async Task ToggleHiringAsync()
    {
        Notice = null;
        var c = (await svc.MyCoachAsync()).Value;
        if (c is null) return;
        var open = !c.IsAvailable;
        if (!await (open ? Confirm.AskAsync("Start taking new trainees?", "Members can hire you again.", "Resume hiring")
                         : Confirm.AskAsync("Pause hiring?", "Members still see you, but they can't hire you until you resume.", "Pause hiring"))) return;
        if (await RunAsync(() => svc.UpdateProfileAsync(c.Bio, c.Specialty, c.HourlyRate, open)))
        {
            IsOpen = open;
            Notice = open ? "Hiring is open. Members can hire you again." : "Hiring is paused. Members still see you, but they cannot hire you until you resume.";
        }
    }

    [RelayCommand]
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

public partial class SessionsViewModel(ISessionService svc, CoachPhotoViewModel? photo = null) : LoadableViewModel
{
    public CoachPhotoViewModel? Photo { get; } = photo;
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    [ObservableProperty] private SessionRow? rescheduling;
    [ObservableProperty] private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan time = new(9, 0, 0);

    public override async Task OnAppearingAsync()
    {
        await LoadAsync();
        if (Photo is not null) await Photo.OnAppearingAsync();
    }

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.SessionsAsync, rows => Fill(Sessions, rows));
    [RelayCommand] private Task CompleteAsync(SessionRow row) => SetAsync(row, Status.Completed);
    [RelayCommand] private Task NoShowAsync(SessionRow row) => SetAsync(row, Status.NoShow);

    [RelayCommand]
    private async Task CancelAsync(SessionRow row)
    {
        Notice = null;
        if (!await Confirm.AskAsync("Cancel this session?", $"With {row.With}, {Fmt.Range(row.Session.ScheduledStart, row.Session.ScheduledEnd)}.\nThey will be told.", "Cancel session")) return;
        if (await RunAsync(() => svc.CancelSessionAsync(row.Session.Id))) { Notice = "Session cancelled. The other person has been told."; await LoadAsync(); }
    }

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

    async Task SetAsync(SessionRow row, string status)
    {
        var (title, button) = status == Status.NoShow ? ("Mark this session as a no-show?", "Mark no-show") : ("Mark this session completed?", "Mark completed");
        if (!await Confirm.AskAsync(title, $"With {row.With}, {Fmt.Range(row.Session.ScheduledStart, row.Session.ScheduledEnd)}.", button)) return;
        if (await RunAsync(() => svc.SetSessionStatusAsync(row.Session.Id, status))) await LoadAsync();
    }
}

/// <summary>One day of the week view with its sessions inline.</summary>
public sealed record WeekRow(CalendarDay Day, IReadOnlyList<CalendarSession> Sessions)
{
    public string Title => Day.Date.ToString("ddd d MMM", CultureInfo.InvariantCulture);
}

// the coach's attendance calendar: a month or a week, a day list underneath, and marks that can be corrected in place
/// <summary>The coach dashboard (D10): this month's numbers, the month/week calendar with attendance marks, and shift posting for the selected day.
/// Without a coach service (older tests) it is the attendance calendar alone.</summary>
public partial class AttendanceViewModel(ISessionService sessions, IAttendanceService attendance, ICoachService? coach = null, IMembershipService? gym = null) : LoadableViewModel
{
    /// <summary>The moment "now" is; tests set a fixed one.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;
    /// <summary>Days are the coach's local days. Tests set a fixed zone.</summary>
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;

    public ObservableCollection<CalendarDay> Days { get; } = new();          // the month grid, 42 cells
    public ObservableCollection<WeekRow> WeekRows { get; } = new();          // the week view, 7 rows
    public ObservableCollection<CalendarSession> DaySessions { get; } = new();
    public ObservableCollection<ShiftPost> DayShifts { get; } = new();
    public int[] HoursOptions { get; } = [2, 4, 6, 8, 10];

    [ObservableProperty] private TimeSpan postTime = new(8, 0, 0);   // the start, as the coach's own clock shows it
    [ObservableProperty] private int postHours = 4;
    [ObservableProperty] private string? postNote;
    [ObservableProperty] private bool canPost;                       // the selected day still has gym time ahead
    public bool HasShifts => coach is not null;
    List<ShiftPost> shifts = [];
    GymSettings? hours;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsMonth), nameof(IsWeek), nameof(PeriodTitle))] private string viewMode = "Month";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PeriodTitle))] private DateOnly focus;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayTitle), nameof(HasDay))] private DateOnly? selectedDay;
    [ObservableProperty] private string sessionsText = "—";
    [ObservableProperty] private string rateText = "—";
    [ObservableProperty] private string upcomingText = "—";
    [ObservableProperty] private string nextText = "";

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
        if (coach is not null && hours is null && gym is not null) { var g = await gym.GymAsync(); if (g.Ok) hours = g.Value; }
        await ReloadAsync();
        Prefill();
    }

    /// <summary>The form for the selected day: opening time, or the next half hour today; Post shift is off when no gym time is left.</summary>
    void Prefill()
    {
        var start = hours is not null && SelectedDay is { } day ? ShiftSlot.DefaultStart(day, hours.OpensAt, hours.ClosesAt, Clock()) : null;
        CanPost = start is not null;
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
        await Task.WhenAll(calendar, coming, posted);
        if (ticket != loadTicket) return;
        var cal = await calendar; var up = await coming;
        if ((await posted).Ok) shifts = (await posted).Value!.ToList();
        if (cal.Ok) { loaded = cal.Value!.ToList(); known = true; } else ErrorMessage = ErrorText.For(cal.Error, cal.Message);   // a failed look leaves what is shown as it was
        if (up.Ok) upcoming = up.Value!.ToList();
        Rebuild();
    }

    void Rebuild()
    {
        var today = Today;
        Sync(Days, AttendanceCalendar.Days(AttendanceCalendar.MonthGrid(Focus.Year, Focus.Month), loaded, today, SelectedDay, new DateOnly(Focus.Year, Focus.Month, 1), Zone), d => d.Date, (a, b) => a == b);
        var week = AttendanceCalendar.Days(AttendanceCalendar.WeekOf(Focus), loaded, today, SelectedDay, null, Zone);
        Fill(WeekRows, week.Select(d => new WeekRow(d, AttendanceCalendar.SessionsOn(loaded, d.Date, Zone))));
        Fill(DaySessions, SelectedDay is { } day ? AttendanceCalendar.SessionsOn(loaded, day, Zone) : Array.Empty<CalendarSession>());
        Fill(DayShifts, SelectedDay is { } sd ? shifts.Where(x => AttendanceCalendar.LocalDay(x.Start, Zone) == sd).OrderBy(x => x.Start) : []);

        var summary = AttendanceCalendar.Summary(AttendanceCalendar.SessionsInMonth(loaded, Focus.Year, Focus.Month, Zone), upcoming);
        SessionsText = known ? summary.SessionsThisMonth.ToString(CultureInfo.InvariantCulture) : "—";   // not a made-up zero while the month is loading or could not load
        RateText = known && summary.RatePercent is { } r ? $"{r}%" : "—";
        UpcomingText = summary.Upcoming.ToString(CultureInfo.InvariantCulture);
        NextText = summary.Next is { } n
            ? $"Next: {AttendanceCalendar.LocalTime(n.Session.ScheduledStart, Zone).ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture)} with {n.With}"
            : "No upcoming sessions.";
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
    /// <summary>Switched off but not deleted: they can be switched back on here.</summary>
    public ObservableCollection<UserRow> Deactivated { get; } = new();
    /// <summary>What can be added: a sub-admin adds members and coaches only (D15).</summary>
    [ObservableProperty] private string[] roles = ["Member", "Coach"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddTitle))] private bool isSuperAdmin;
    public string AddTitle => IsSuperAdmin ? "Add a member, coach or employee" : "Add a member or coach";

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
        if (archive is not null)
            await RunAsync(archive.CanManageAsync, allowed => CanDeleteAccounts = allowed);
        await RunAsync(svc.IsSuperAdminAsync, yes => IsSuperAdmin = yes);
        Roles = IsSuperAdmin ? ["Member", "Coach", "Employee"] : ["Member", "Coach"];
        if (!Roles.Contains(NewRole)) NewRole = "Member";
        await RunAsync(svc.UsersAsync, rows => { Fill(Users, rows.Where(r => r.IsActive)); Fill(Deactivated, rows.Where(r => !r.IsActive)); });
    }

    [RelayCommand]
    private async Task AddUserAsync()
    {
        Notice = null;
        if (!await Confirm.AskAsync($"Add {NameParts.Join(NewFirstName, NewLastName)}?", $"{NewEmail.Trim()}\nRole: {NewRole}\nThey sign in with the temporary password, then pick their own.", "Add user")) return;
        if (!await RunAsync(() => svc.CreateUserAsync(NewFirstName, NewLastName, NewEmail, NewRole.ToLowerInvariant(), NewPassword))) return;
        Notice = $"{NameParts.Join(NewFirstName, NewLastName)} can sign in now with that email and password.";
        NewFirstName = NewLastName = NewEmail = NewPassword = "";
        await LoadAsync();
    }

    [RelayCommand] private void StartReset(UserRow user) { Resetting = user; ResetPassword = ""; Notice = null; }
    [RelayCommand] private void StopReset() => Resetting = null;

    [RelayCommand]
    private async Task ConfirmResetAsync()
    {
        if (Resetting is not { } user) return;
        if (!await RunAsync(() => svc.ResetPasswordAsync(user.UserId, ResetPassword))) return;
        Notice = $"{user.Username} can sign in with the new temporary password.";
        Resetting = null;
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

public partial class CatalogAdminViewModel(IAdminService svc, IProofPicker? picker = null) : LoadableViewModel
{
    /// <summary>Only the super admin archives (D15); a sub-admin who tries is told so.</summary>
    [ObservableProperty] private bool canArchive;

    public ObservableCollection<MembershipPackage> Packages { get; } = new();
    public ObservableCollection<Amenity> Amenities { get; } = new();
    [ObservableProperty] private string newPackageName = "";
    [ObservableProperty] private string newDescription = "";
    [ObservableProperty] private decimal newPrice = 30;
    [ObservableProperty] private int newDays = 30;
    [ObservableProperty] private string newAmenityName = "";
    [ObservableProperty] private decimal newAmenityPrice = 200;
    [ObservableProperty] private int newAmenityDays = 30;
    [ObservableProperty] private string newAmenityDescription = "";
    // like packages: one form adds an amenity, or edits the one held here
    [ObservableProperty, NotifyPropertyChangedFor(nameof(AmenitySaveLabel), nameof(IsEditingAmenity))] private Amenity? editingAmenity;
    public string AmenitySaveLabel => EditingAmenity is null ? "Add amenity" : "Save changes";
    public bool IsEditingAmenity => EditingAmenity is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveLabel), nameof(IsEditing))] private MembershipPackage? editing;

    public string SaveLabel => Editing is null ? "Add package" : "Save changes";
    public bool IsEditing => Editing is not null;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(svc.IsSuperAdminAsync, yes => CanArchive = yes);
        await RunAsync(svc.AllPackagesAsync, r => Fill(Packages, r.Where(p => p.IsActive)));
        await RunAsync(svc.AllAmenitiesAsync, r => Fill(Amenities, r.Where(a => a.IsActive)));
    }

    // one form serves both "add" and "edit": Editing holds the package being changed, or null for a new one
    [RelayCommand]
    private async Task SavePackageAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPackageName)) { ErrorMessage = "Give the package a name."; return; }
        if (NewPrice <= 0 || NewDays <= 0) { ErrorMessage = "Price and days must be more than zero."; return; }
        if (!await Confirm.AskAsync(Editing is null ? $"Add {NewPackageName.Trim()}?" : $"Save changes to {NewPackageName.Trim()}?",
                $"₱{NewPrice:N2} for {NewDays} days.", Editing is null ? "Add package" : "Save changes")) return;
        var p = Editing ?? new MembershipPackage();
        p.Name = NewPackageName.Trim(); p.Description = string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim();
        p.Price = NewPrice; p.DurationDays = NewDays;
        if (await RunAsync(() => svc.SavePackageAsync(p))) { CancelEdit(); await LoadAsync(); }
    }

    [RelayCommand]
    private void EditPackage(MembershipPackage p)
    { Editing = p; NewPackageName = p.Name; NewDescription = p.Description ?? ""; NewPrice = p.Price; NewDays = p.DurationDays; }

    [RelayCommand]
    private void CancelEdit()
    { Editing = null; NewPackageName = ""; NewDescription = ""; NewPrice = 30; NewDays = 30; }

    // delete asks first: DeletePackage picks the row, ConfirmDelete does it
    [ObservableProperty] private MembershipPackage? deleting;

    [RelayCommand]
    private void DeletePackage(MembershipPackage p)
    {
        if (!CanArchive) { ErrorMessage = "Only the super admin can archive a package. Turn it off instead to stop selling it."; return; }
        Deleting = p;
    }
    [RelayCommand] private void KeepPackage() => Deleting = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (Deleting is not { } p) return;
        Deleting = null;
        if (!await RunAsync(() => svc.DeletePackageAsync(p))) return;
        if (Editing?.Id == p.Id) CancelEdit();
        await LoadAsync();
    }

    [RelayCommand]
    private async Task TogglePackageAsync(MembershipPackage p)
    {
        if (!await (p.IsActive ? Confirm.AskAsync($"Stop selling {p.Name}?", "Members can't buy it until you turn it back on. Anyone who has it keeps it.", "Turn off")
                               : Confirm.AskAsync($"Sell {p.Name} again?", "Members can buy it again.", "Turn on"))) return;
        p.IsActive = !p.IsActive; if (await RunAsync(() => svc.SavePackageAsync(p))) await LoadAsync(); else p.IsActive = !p.IsActive; }

    [RelayCommand]
    private async Task AddAmenityAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAmenityName)) { ErrorMessage = "Give the amenity a name."; return; }
        if (NewAmenityPrice < 0 || NewAmenityDays <= 0) { ErrorMessage = "Price can't be negative and days must be more than zero."; return; }
        if (!await Confirm.AskAsync(EditingAmenity is null ? $"Add {NewAmenityName.Trim()}?" : $"Save changes to {NewAmenityName.Trim()}?",
                $"Pass: ₱{NewAmenityPrice:N2} for {NewAmenityDays} days.", EditingAmenity is null ? "Add amenity" : "Save changes")) return;
        var a = EditingAmenity ?? new Amenity();
        a.Name = NewAmenityName.Trim(); a.Price = NewAmenityPrice; a.DurationDays = NewAmenityDays;
        a.Description = string.IsNullOrWhiteSpace(NewAmenityDescription) ? null : NewAmenityDescription.Trim();
        if (await RunAsync(() => svc.SaveAmenityAsync(a))) { CancelAmenityEdit(); await LoadAsync(); }
    }

    [RelayCommand]
    private void EditAmenity(Amenity a)
    { EditingAmenity = a; NewAmenityName = a.Name; NewAmenityPrice = a.Price; NewAmenityDays = a.DurationDays ?? 30; NewAmenityDescription = a.Description ?? ""; }

    [RelayCommand]
    private void CancelAmenityEdit()
    { EditingAmenity = null; NewAmenityName = ""; NewAmenityPrice = 200; NewAmenityDays = 30; NewAmenityDescription = ""; }

    [RelayCommand]
    private async Task UploadAmenityPhotoAsync(Amenity amenity)
    {
        if (IsBusy || picker is null) return;
        IsBusy = true; ErrorMessage = Notice = null;
        try
        {
            if (await picker.PickAsync() is not { } file) return;
            var result = await svc.SetAmenityPhotoAsync(amenity.Id, file.FileName, file.Data);
            if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
            await LoadAsync();
            Notice = $"Photo updated for {amenity.Name}.";
        }
        catch (Exception ex) { ErrorMessage = ErrorText.For(ErrorMapper.Map(ex), ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleAmenityAsync(Amenity a)
    {
        if (!await (a.IsActive ? Confirm.AskAsync($"Stop selling {a.Name} passes?", "Members can't buy a pass until you turn it back on. Passes already bought keep working.", "Turn off")
                               : Confirm.AskAsync($"Sell {a.Name} passes again?", "Members can buy a pass again.", "Turn on"))) return;
        a.IsActive = !a.IsActive; if (await RunAsync(() => svc.SaveAmenityAsync(a))) await LoadAsync(); else a.IsActive = !a.IsActive; }
}

public partial class DiscountsViewModel(IAdminService svc, ArchiveViewModel? archive = null) : LoadableViewModel
{
    public ArchiveViewModel? Archive { get; } = archive;
    /// <summary>Only the super admin archives codes and opens Archive (D15).</summary>
    [ObservableProperty] private bool canArchive;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowDiscounts))] private bool showArchive;
    public bool ShowDiscounts => !ShowArchive;
    [RelayCommand] private async Task OpenArchiveAsync() { if (Archive is null || !CanArchive) return; ShowArchive = true; await Archive.OnAppearingAsync(); }
    [RelayCommand] private async Task OpenDiscountsAsync() { ShowArchive = false; await LoadAsync(); }
    public ObservableCollection<DiscountRow> Discounts { get; } = new();
    [ObservableProperty] private string newCode = "";
    [ObservableProperty] private int newPercent = 10;
    [ObservableProperty] private DateTime validUntil = DateTime.Today.AddDays(30);
    [ObservableProperty] private int newLimit = 100;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(svc.IsSuperAdminAsync, yes => CanArchive = yes);
        await RunAsync(svc.DiscountsAsync, rows => Fill(Discounts, rows.Where(r => r.Discount.IsActive && r.Discount.ExpiresAt > DateTime.UtcNow)));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        Notice = null;
        // valid through the end of the chosen day, local time
        var d = new Discount { Code = NewCode, Percent = NewPercent, MaxUses = NewLimit, ExpiresAt = ValidUntil.Date.AddDays(1).ToUniversalTime() };
        if (!await RunAsync(() => svc.SaveDiscountAsync(d))) return;
        Notice = $"Code {d.Code} is live.";
        NewCode = "";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleAsync(DiscountRow row)
    {
        var d = row.Discount;
        if (!await (d.IsActive ? Confirm.AskAsync($"Turn off code {d.Code}?", "Members can't use it until you turn it back on.", "Turn off")
                               : Confirm.AskAsync($"Turn on code {d.Code}?", "Members can use it again.", "Turn on"))) return;
        d.IsActive = !d.IsActive;
        if (await RunAsync(() => svc.SaveDiscountAsync(d))) await LoadAsync(); else d.IsActive = !d.IsActive;
    }

    [ObservableProperty] private DiscountRow? editing;
    [ObservableProperty] private int editLimit;

    [RelayCommand] private void ChangeLimit(DiscountRow row) { Editing = row; EditLimit = row.Discount.MaxUses ?? Math.Max(1, (int)(row.Used + row.Pending)); Notice = null; }
    [RelayCommand] private void CancelEdit() => Editing = null;

    [RelayCommand]
    private async Task SaveLimitAsync()
    {
        if (Editing is not { } row) return;
        if (!await Confirm.AskAsync($"Change the limit for {row.Discount.Code}?", $"It can be used {EditLimit} times in total.", "Change limit")) return;
        var old = row.Discount.MaxUses; row.Discount.MaxUses = EditLimit;
        if (!await RunAsync(() => svc.SaveDiscountAsync(row.Discount))) { row.Discount.MaxUses = old; return; }
        Editing = null;
        Notice = $"Code {row.Discount.Code} can now be used {EditLimit} times.";
        await LoadAsync();
    }

    [ObservableProperty] private DiscountRow? deleting;

    [RelayCommand]
    private void Delete(DiscountRow row)
    {
        if (!CanArchive) { ErrorMessage = "Only the super admin can archive a code. Turn it off instead."; return; }
        Deleting = row;
    }
    [RelayCommand] private void Keep() => Deleting = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (Deleting is not { } row) return;
        Deleting = null;
        if (await RunAsync(() => svc.DeleteDiscountAsync(row.Discount))) await LoadAsync();
    }
}

public partial class RevenueViewModel(IAdminService svc, IReportFiles? files = null) : LoadableViewModel
{
    public ObservableCollection<RevenueRow> Rows { get; } = new();
    [ObservableProperty] private DateTime from = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime to = DateTime.Today;
    [ObservableProperty] private string totalText = "₱0.00";
    // what the screen shows; the PDF uses this, not dates edited since
    (DateOnly From, DateOnly To, decimal Total)? _loaded;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => LoadRangeAsync(DateOnly.FromDateTime(From), DateOnly.FromDateTime(To));

    Task<bool> LoadRangeAsync(DateOnly from, DateOnly to) => RunAsync(() => svc.RevenueAsync(from, to), rows =>
    {
        Fill(Rows, rows);
        TotalText = $"₱{rows.Sum(r => r.Total):N2}";
        _loaded = (from, to, rows.Sum(r => r.Total));
        ExportPdfCommand.NotifyCanExecuteChanged();
    });

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

// admin: the gym's name, the payment QR members scan, and the walk-in price
public partial class GymSettingsViewModel(IAdminService svc, IProofPicker picker) : LoadableViewModel
{
    [ObservableProperty] private string gymName = "";
    [ObservableProperty] private string? qrAccountName;
    [ObservableProperty] private decimal walkInPrice;
    [ObservableProperty] private TimeSpan opensAt = TimeSpan.FromHours(6);
    [ObservableProperty] private TimeSpan closesAt = TimeSpan.FromHours(22);
    [ObservableProperty] private string qrStatus = "No QR uploaded yet. Members can't pay until you add one.";
    GymSettings? _loaded;
    (string FileName, byte[] Data)? _newQr;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.GymSettingsAsync, g =>
    {
        _loaded = g; GymName = g.GymName; QrAccountName = g.QrAccountName; WalkInPrice = g.WalkInPrice; OpensAt = g.OpensAt; ClosesAt = g.ClosesAt;
        QrStatus = string.IsNullOrWhiteSpace(g.QrImagePath) ? "No QR uploaded yet. Members can't pay until you add one." : "A payment QR is set. Choose a new image to replace it.";
    });

    [RelayCommand]
    private async Task ChooseQrAsync()
    {
        if (await picker.PickAsync() is not { } file) return;
        _newQr = file;
        QrStatus = $"Ready to upload {file.FileName}. Save to apply it.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Notice = null;
        if (_loaded is null) return;
        var qr = _newQr is null ? "" : "\nThe payment QR members scan will change.";
        if (!await Confirm.AskAsync("Save gym settings?", $"Open {DateTime.Today.Add(OpensAt):h:mm tt} to {DateTime.Today.Add(ClosesAt):h:mm tt} every day.{qr}", "Save settings")) return;
        _loaded.GymName = GymName.Trim(); _loaded.QrAccountName = string.IsNullOrWhiteSpace(QrAccountName) ? null : QrAccountName.Trim(); _loaded.WalkInPrice = WalkInPrice;
        _loaded.OpensAt = OpensAt; _loaded.ClosesAt = ClosesAt;
        if (!await RunAsync(() => svc.SaveGymSettingsAsync(_loaded, _newQr))) return;
        _newQr = null;
        Notice = "Saved.";
        await LoadAsync();
    }
}

public partial class AuditViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<AuditRow> Entries { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.AuditAsync, rows => Fill(Entries, rows));
}

public sealed record StatusChoice(string Value, string Label);

/// <summary>Front desk and admin: mark an amenity available, in use or unavailable, and see who was alerted.</summary>
public partial class AmenityStatusViewModel(IAmenityService svc) : LoadableViewModel
{
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

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.BoardAsync, board => Fill(Cards, board.Cards));

    // Quiet and in place: the error line, the notice and the open form stay as they are, and only cards that changed are swapped.
    public override async Task AutoRefreshAsync()
    {
        if (IsBusy) return;
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
