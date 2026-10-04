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

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.SessionsAsync, rows => Fill(Sessions, rows));
    [RelayCommand] private Task CompleteAsync(SessionRow row) => SetAsync(row, Status.Completed);
    [RelayCommand] private Task CancelAsync(SessionRow row) => SetAsync(row, Status.Cancelled);
    [RelayCommand] private Task NoShowAsync(SessionRow row) => SetAsync(row, Status.NoShow);

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

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task LoadAsync() => RunAsync(svc.UsersAsync, rows => Fill(Users, rows));

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
    [ObservableProperty] private decimal newPrice = 30;
    [ObservableProperty] private int newDays = 30;
    [ObservableProperty] private string newAmenityName = "";

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(svc.AllPackagesAsync, r => Fill(Packages, r));
        await RunAsync(svc.AllAmenitiesAsync, r => Fill(Amenities, r));
    }

    [RelayCommand]
    private async Task AddPackageAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPackageName)) { ErrorMessage = "Give the package a name."; return; }
        if (await RunAsync(() => svc.SavePackageAsync(new MembershipPackage { Name = NewPackageName.Trim(), Price = NewPrice, DurationDays = NewDays })))
        { NewPackageName = ""; await LoadAsync(); }
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
