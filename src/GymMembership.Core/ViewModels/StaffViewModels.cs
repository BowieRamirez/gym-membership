using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

// ---------- front desk ----------
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

// front desk: cash and GCash at the counter, and walk-ins. Verified straight away with a receipt number
public partial class CounterViewModel(IPaymentStaffService svc, IAttendanceService members) : LoadableViewModel
{
    public string[] Kinds { get; } = ["Membership", "Renewal", "Amenity", "Walk-in"];
    public string[] Methods { get; } = ["Cash", "GCash", "Bank transfer"];
    public ObservableCollection<MemberHit> Hits { get; } = new();
    public ObservableCollection<CounterItem> Items { get; } = new();
    public ObservableCollection<CashRow> CashToday { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWalkIn), nameof(NeedsMember), nameof(NeedsReference))] private string kind = "Membership";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(NeedsReference))] private string method = "Cash";
    [ObservableProperty] private string search = "";
    [ObservableProperty] private MemberHit? member;
    [ObservableProperty] private CounterItem? item;
    [ObservableProperty] private string walkInName = "";
    [ObservableProperty] private string referenceNo = "";
    [ObservableProperty] private string discountCode = "";
    [ObservableProperty] private string cashTotal = "₱0.00";

    public bool IsWalkIn => Kind == "Walk-in";
    public bool NeedsMember => !IsWalkIn;
    public bool NeedsReference => Method != "Cash";

    static string KindKey(string k) => k switch { "Renewal" => PayKind.Renewal, "Amenity" => PayKind.Amenity, "Walk-in" => PayKind.WalkIn, _ => PayKind.Membership };
    static string MethodKey(string m) => m switch { "GCash" => PayMethod.Gcash, "Bank transfer" => PayMethod.BankTransfer, _ => PayMethod.Cash };

    public override async Task OnAppearingAsync() { await RefreshItemsAsync(); await LoadCashAsync(); }

    partial void OnKindChanged(string value) { Item = null; _ = RefreshItemsAsync(); }
    partial void OnMemberChanged(MemberHit? value) { Item = null; if (Kind == "Renewal") _ = RefreshItemsAsync(); }

    async Task RefreshItemsAsync()
    {
        if (IsWalkIn) { Items.Clear(); return; }
        await RunAsync(() => svc.ItemsAsync(KindKey(Kind), Member?.MemberId), rows => Fill(Items, rows));
    }

    async Task LoadCashAsync() =>
        await RunAsync(() => svc.CashSummaryAsync(DateOnly.FromDateTime(DateTime.Today)), rows =>
        { Fill(CashToday, rows); CashTotal = $"₱{rows.Sum(r => r.Total):N2}"; });

    [RelayCommand] private Task FindAsync() => RunAsync(() => members.FindMembersAsync(Search), rows => Fill(Hits, rows));

    [RelayCommand]
    private async Task RecordAsync()
    {
        Notice = null;
        if (!IsWalkIn && (Member is null || Item is null)) { ErrorMessage = "Choose the member and what they are paying for."; return; }
        Payment? paid = null;
        if (!await RunAsync(() => svc.RecordCounterAsync(KindKey(Kind), MethodKey(Method), IsWalkIn ? null : Member!.MemberId, Item?.Id,
                IsWalkIn ? WalkInName : null, NeedsReference ? ReferenceNo : null, IsWalkIn ? null : DiscountCode), p => paid = p)) return;
        Notice = $"Recorded ₱{paid!.Amount:N2}. Receipt {paid.ReceiptNo}.";
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

public partial class CheckInViewModel(IAttendanceService svc) : LoadableViewModel
{
    public ObservableCollection<MemberHit> Hits { get; } = new();
    [ObservableProperty] private string search = "";

    public override Task OnAppearingAsync() => SearchAsync();

    [RelayCommand] private Task SearchAsync() => RunAsync(() => svc.FindMembersAsync(Search), rows => Fill(Hits, rows));

    [RelayCommand]
    private async Task CheckInAsync(MemberHit hit)
    {
        Notice = null;
        if (await RunAsync(() => svc.CheckInAsync(hit.MemberId))) Notice = $"{hit.Username} is checked in.";
    }
}

// ---------- coach ----------
public partial class CoachProfileViewModel(ICoachService svc) : LoadableViewModel
{
    [ObservableProperty] private string? bio;
    [ObservableProperty] private string? specialty;
    [ObservableProperty] private decimal? hourlyRate;
    [ObservableProperty] private bool isAvailable;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.MyCoachAsync, c =>
    { if (c is null) return; Bio = c.Bio; Specialty = c.Specialty; HourlyRate = c.HourlyRate; IsAvailable = c.IsAvailable; });

    [RelayCommand]
    private async Task SaveAsync()
    { if (await RunAsync(() => svc.UpdateProfileAsync(Bio, Specialty, HourlyRate, IsAvailable))) Notice = "Profile saved."; }
}

public partial class TraineesViewModel(ICoachService svc, IMessageService messages, IAppNavigator nav) : LoadableViewModel
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
        if (await RunAsync(() => svc.EndHireAsync(row.HireId))) { Notice = $"{row.Name} is no longer your trainee."; await LoadAsync(); }
    }
}

public partial class ShiftsViewModel(ICoachService svc) : LoadableViewModel
{
    public ObservableCollection<ShiftPost> Shifts { get; } = new();
    public int[] HoursOptions { get; } = [2, 4, 6, 8, 10];

    [ObservableProperty] private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan time = new(8, 0, 0);
    [ObservableProperty] private int hours = 6;
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
    { if (await RunAsync(() => svc.CancelShiftAsync(shift.Id))) await LoadAsync(); }
}

public partial class RequestsViewModel(ISessionService svc) : LoadableViewModel
{
    public ObservableCollection<RequestRow> Requests { get; } = new();
    public ObservableCollection<CoachCard> Coaches { get; } = new();
    public int[] Durations { get; } = [30, 45, 60, 90];

    [ObservableProperty] private bool isCoach;
    [ObservableProperty] private bool canCompose;
    [ObservableProperty] private CoachCard? selectedCoach;
    [ObservableProperty] private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan time = new(9, 0, 0);
    [ObservableProperty] private int durationMinutes = 60;
    [ObservableProperty] private string? message;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.RequestsAsync, v =>
    {
        Fill(Requests, v.Rows); Fill(Coaches, v.HiredCoaches);
        IsCoach = v.IsCoach;
        CanCompose = v.IsCoach || v.HiredCoaches.Count > 0;
        SelectedCoach ??= Coaches.FirstOrDefault();
    });

    [RelayCommand]
    private async Task SubmitAsync()
    {
        Notice = null;
        var start = (Date.Date + Time).ToUniversalTime();
        var end = start.AddMinutes(DurationMinutes);
        if (!IsCoach && SelectedCoach is null) { ErrorMessage = "Choose a coach first."; return; }
        var ok = IsCoach
            ? await RunAsync(() => svc.PostAvailabilityAsync(start, end))
            : await RunAsync(() => svc.RequestAsync(SelectedCoach!.Coach.UserId, start, end, Message));
        if (ok) { Notice = IsCoach ? "Availability posted." : "Request sent."; Message = null; await LoadAsync(); }
    }

    [RelayCommand] private Task ApproveAsync(RequestRow row) => RespondAsync(row, true);
    [RelayCommand] private Task RejectAsync(RequestRow row) => RespondAsync(row, false);

    [RelayCommand]
    private async Task CancelAsync(RequestRow row)
    { if (await RunAsync(() => svc.CancelRequestAsync(row.Request.Id))) await LoadAsync(); }

    async Task RespondAsync(RequestRow row, bool approve)
    {
        Notice = null;
        if (await RunAsync(() => svc.RespondAsync(row.Request.Id, approve)))
        { Notice = approve ? "Approved. The session is on the calendar." : "Rejected."; await LoadAsync(); }
    }
}

public partial class SessionsViewModel(ISessionService svc) : LoadableViewModel
{
    public ObservableCollection<SessionRow> Sessions { get; } = new();
    [ObservableProperty] private SessionRow? rescheduling;
    [ObservableProperty] private DateTime date = DateTime.Today.AddDays(1);
    [ObservableProperty] private TimeSpan time = new(9, 0, 0);

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.SessionsAsync, rows => Fill(Sessions, rows));
    [RelayCommand] private Task CompleteAsync(SessionRow row) => SetAsync(row, Status.Completed);
    [RelayCommand] private Task NoShowAsync(SessionRow row) => SetAsync(row, Status.NoShow);

    [RelayCommand]
    private async Task CancelAsync(SessionRow row)
    {
        Notice = null;
        if (await RunAsync(() => svc.CancelSessionAsync(row.Session.Id))) { Notice = "Session cancelled. The other person has been told."; await LoadAsync(); }
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
        if (await RunAsync(() => svc.RescheduleAsync(row.Session.Id, start, end)))
        { Notice = "Reschedule sent. It moves once the other person approves it in Time requests."; Rescheduling = null; }
    }

    async Task SetAsync(SessionRow row, string status)
    { if (await RunAsync(() => svc.SetSessionStatusAsync(row.Session.Id, status))) await LoadAsync(); }
}

public partial class AttendanceViewModel(ISessionService sessions, IAttendanceService attendance) : LoadableViewModel
{
    public ObservableCollection<SessionRow> Sessions { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(sessions.SessionsAsync, rows =>
        Fill(Sessions, rows.Where(r => r.IsMine && r.Session.Status is Status.Scheduled or Status.Completed)));

    // argument is "<sessionId>|<status>" so one XAML button template serves all four marks
    [RelayCommand]
    private async Task MarkAsync(string arg)
    {
        var parts = arg.Split('|');
        var row = Sessions.First(s => s.Session.Id == int.Parse(parts[0]));
        Notice = null;
        if (await RunAsync(() => attendance.RecordAsync(row.Session.Id, parts[1])))
            Notice = $"Marked {row.With} as {parts[1]}.";
    }
}

// ---------- admin ----------
public partial class UsersViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<UserRow> Users { get; } = new();
    public string[] Roles { get; } = ["Member", "Coach", "Employee"];
    [ObservableProperty] private UserRow? resetting;
    [ObservableProperty] private string resetPassword = "";
    [ObservableProperty] private string newName = "";
    [ObservableProperty] private string newEmail = "";
    [ObservableProperty] private string newPassword = "";
    [ObservableProperty] private string newRole = "Member";

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.UsersAsync, rows => Fill(Users, rows));

    [RelayCommand]
    private async Task AddUserAsync()
    {
        Notice = null;
        if (!await RunAsync(() => svc.CreateUserAsync(NewName, NewEmail, NewRole.ToLowerInvariant(), NewPassword))) return;
        Notice = $"{NewName.Trim()} can sign in now with that email and password.";
        NewName = NewEmail = NewPassword = "";
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
        var ok = user.Roles.Contains(parts[1])
            ? await RunAsync(() => svc.RevokeRoleAsync(user.UserId, parts[1]))
            : await RunAsync(() => svc.AssignRoleAsync(user.UserId, parts[1]));
        if (ok) await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleActiveAsync(UserRow user)
    { if (await RunAsync(() => svc.SetActiveAsync(user.UserId, !user.IsActive))) await LoadAsync(); }
}

public partial class CatalogAdminViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<MembershipPackage> Packages { get; } = new();
    public ObservableCollection<Amenity> Amenities { get; } = new();
    [ObservableProperty] private string newPackageName = "";
    [ObservableProperty] private string newDescription = "";
    [ObservableProperty] private decimal newPrice = 30;
    [ObservableProperty] private int newDays = 30;
    [ObservableProperty] private string newAmenityName = "";
    [ObservableProperty] private decimal newAmenityPrice = 200;
    [ObservableProperty] private int newAmenityDays = 30;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SaveLabel), nameof(IsEditing))] private MembershipPackage? editing;

    public string SaveLabel => Editing is null ? "Add package" : "Save changes";
    public bool IsEditing => Editing is not null;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(svc.AllPackagesAsync, r => Fill(Packages, r));
        await RunAsync(svc.AllAmenitiesAsync, r => Fill(Amenities, r));
    }

    // one form serves both "add" and "edit": Editing holds the package being changed, or null for a new one
    [RelayCommand]
    private async Task SavePackageAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPackageName)) { ErrorMessage = "Give the package a name."; return; }
        if (NewPrice <= 0 || NewDays <= 0) { ErrorMessage = "Price and days must be more than zero."; return; }
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

    [RelayCommand] private void DeletePackage(MembershipPackage p) => Deleting = p;
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
    { p.IsActive = !p.IsActive; if (await RunAsync(() => svc.SavePackageAsync(p))) await LoadAsync(); else p.IsActive = !p.IsActive; }

    [RelayCommand]
    private async Task AddAmenityAsync()
    {
        if (string.IsNullOrWhiteSpace(NewAmenityName)) { ErrorMessage = "Give the amenity a name."; return; }
        if (NewAmenityPrice < 0 || NewAmenityDays <= 0) { ErrorMessage = "Price can't be negative and days must be more than zero."; return; }
        if (await RunAsync(() => svc.SaveAmenityAsync(new Amenity { Name = NewAmenityName.Trim(), Price = NewAmenityPrice, DurationDays = NewAmenityDays })))
        { NewAmenityName = ""; await LoadAsync(); }
    }

    [RelayCommand]
    private async Task ToggleAmenityAsync(Amenity a)
    { a.IsActive = !a.IsActive; if (await RunAsync(() => svc.SaveAmenityAsync(a))) await LoadAsync(); else a.IsActive = !a.IsActive; }
}

public partial class DiscountsViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<DiscountRow> Discounts { get; } = new();
    [ObservableProperty] private string newCode = "";
    [ObservableProperty] private int newPercent = 10;
    [ObservableProperty] private DateTime validUntil = DateTime.Today.AddDays(30);

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.DiscountsAsync, rows => Fill(Discounts, rows));

    [RelayCommand]
    private async Task AddAsync()
    {
        Notice = null;
        // valid through the end of the chosen day, local time
        var d = new Discount { Code = NewCode, Percent = NewPercent, ExpiresAt = ValidUntil.Date.AddDays(1).ToUniversalTime() };
        if (!await RunAsync(() => svc.SaveDiscountAsync(d))) return;
        Notice = $"Code {d.Code} is live.";
        NewCode = "";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleAsync(DiscountRow row)
    {
        var d = row.Discount; d.IsActive = !d.IsActive;
        if (await RunAsync(() => svc.SaveDiscountAsync(d))) await LoadAsync(); else d.IsActive = !d.IsActive;
    }

    [ObservableProperty] private DiscountRow? deleting;

    [RelayCommand] private void Delete(DiscountRow row) => Deleting = row;
    [RelayCommand] private void Keep() => Deleting = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (Deleting is not { } row) return;
        Deleting = null;
        if (await RunAsync(() => svc.DeleteDiscountAsync(row.Discount))) await LoadAsync();
    }
}

public partial class RevenueViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<RevenueRow> Rows { get; } = new();
    [ObservableProperty] private DateTime from = DateTime.Today.AddDays(-30);
    [ObservableProperty] private DateTime to = DateTime.Today;
    [ObservableProperty] private string totalText = "₱0.00";

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(() => svc.RevenueAsync(DateOnly.FromDateTime(From), DateOnly.FromDateTime(To)), rows =>
    {
        Fill(Rows, rows);
        TotalText = $"₱{rows.Sum(r => r.Total):N2}";
    });
}

// admin: the gym's name, the payment QR members scan, and the walk-in price
public partial class GymSettingsViewModel(IAdminService svc, IProofPicker picker) : LoadableViewModel
{
    [ObservableProperty] private string gymName = "";
    [ObservableProperty] private string? qrAccountName;
    [ObservableProperty] private decimal walkInPrice;
    [ObservableProperty] private string qrStatus = "No QR uploaded yet. Members can't pay until you add one.";
    GymSettings? _loaded;
    (string FileName, byte[] Data)? _newQr;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.GymSettingsAsync, g =>
    {
        _loaded = g; GymName = g.GymName; QrAccountName = g.QrAccountName; WalkInPrice = g.WalkInPrice;
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
        _loaded.GymName = GymName.Trim(); _loaded.QrAccountName = string.IsNullOrWhiteSpace(QrAccountName) ? null : QrAccountName.Trim(); _loaded.WalkInPrice = WalkInPrice;
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
        var unchanged = card.Availability == Choice.Value && (card.Reason ?? "") == reason && (!ShowDetail || card.BackAt?.ToUniversalTime().ToString("s") == back?.ToString("s"));

        Notice = null;
        IReadOnlyList<NotifiedMember>? told = null;
        if (!await RunAsync(() => svc.SetStatusAsync(card.Amenity.Id, Choice.Value, reason, back), r => told = r)) return;   // the editor stays open with what was typed

        Notice = unchanged ? $"Nothing changed for {card.Amenity.Name}." : Summary(card.Amenity.Name, Choice, told!);
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
