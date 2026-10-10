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

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.PendingAsync, rows => Fill(Pending, rows));
    [RelayCommand] private Task ApproveAsync(PaymentRow row) => DecideAsync(row, true);
    [RelayCommand] private Task RejectAsync(PaymentRow row) => DecideAsync(row, false);

    async Task DecideAsync(PaymentRow row, bool approve)
    {
        Notice = null;
        if (await RunAsync(() => svc.VerifyAsync(row.Payment.Id, approve)))
        { Pending.Remove(row); Notice = approve ? $"Verified {row.Payer}'s payment." : $"Rejected {row.Payer}'s payment."; }
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

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.MyShiftsAsync, rows => Fill(Shifts, rows));

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
            : await RunAsync(() => svc.RequestAsync(SelectedCoach!.Coach.Id, start, end, Message));
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
        if (await RunAsync(() => attendance.RecordAsync(row.Session.Id, row.Session.MemberId, parts[1])))
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
        if (await RunAsync(() => svc.SaveAmenityAsync(new Amenity { Name = NewAmenityName.Trim() }))) { NewAmenityName = ""; await LoadAsync(); }
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
    [ObservableProperty] private string totalText = "0.00";

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(() => svc.RevenueAsync(DateOnly.FromDateTime(From), DateOnly.FromDateTime(To)), rows =>
    {
        Fill(Rows, rows);
        TotalText = $"{rows.Sum(r => r.Total):N2} {rows.FirstOrDefault()?.Cur ?? "USD"}";
    });
}

public partial class AuditViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<AuditRow> Entries { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.AuditAsync, rows => Fill(Entries, rows));
}
