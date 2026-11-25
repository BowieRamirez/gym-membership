using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

/// <summary>One coach or front desk person: who, their role, how they are paid, active or not.</summary>
public sealed record EmployeeRow(UserRow User, StaffSalary? Pay)
{
    public string Name => User.Username;
    public string Initials => Services.Initials.From(User.Username);
    public string Email => User.Email;
    public string RoleText => User.IsCoach && User.IsEmployee ? "Coach, Front desk" : User.IsCoach ? "Coach" : "Front desk";
    public string PayText => Pay is { Amount: { } a } p ? PayTypes.RateText(p.PayType, a) : "No pay set";
    public bool HasPay => Pay?.Amount is not null;
    public string Status => User.ActiveStatus;
}

/// <summary>
/// Owner, 2026-11-25: Employees is its own page, not Users and roles: coaches and front desk only, a filter (All, Coaches, Front desk),
/// and no adding accounts (that stays on Users and roles; pay is set on Payroll's Salary Configuration).
/// </summary>
public partial class EmployeesViewModel(IAdminService svc) : LoadableViewModel
{
    List<EmployeeRow> _all = [];

    public ObservableCollection<EmployeeRow> Rows { get; } = new();
    public IReadOnlyList<string> FilterOptions { get; } = ["All", "Coaches", "Front desk"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsAll), nameof(IsCoaches), nameof(IsFrontDesk))] private string filter = "All";
    public bool IsAll => Filter == "All";
    public bool IsCoaches => Filter == "Coaches";
    public bool IsFrontDesk => Filter == "Front desk";
    [ObservableProperty] private string countText = "";

    public override Task OnAppearingAsync() => LoadAsync();
    partial void OnFilterChanged(string value) => Show();
    [RelayCommand] private void PickFilter(string value) => Filter = value;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IReadOnlyList<UserRow> users = [];
        IReadOnlyList<StaffSalary> pay = [];
        if (!await RunAsync(svc.UsersAsync, u => users = u)) return;
        await RunAsync(svc.PayrollStaffAsync, p => pay = p);   // pay is a help, not a must: the list shows without it
        var byId = pay.ToDictionary(p => p.UserId);
        _all = users.Where(u => u.IsCoach || u.IsEmployee)
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => new EmployeeRow(u, byId.GetValueOrDefault(u.UserId))).ToList();
        Show();
    }

    void Show()
    {
        var shown = Filter switch
        {
            "Coaches" => _all.Where(r => r.User.IsCoach),
            "Front desk" => _all.Where(r => r.User.IsEmployee),
            _ => _all,
        };
        Fill(Rows, shown);
        var n = Rows.Count;
        CountText = Filter switch
        {
            "Coaches" => n == 1 ? "1 coach" : $"{n} coaches",
            "Front desk" => $"{n} front desk",
            _ => n == 1 ? "1 person" : $"{n} people",
        };
    }
}
