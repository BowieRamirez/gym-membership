using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class ArchiveViewModel(IArchiveService service, IAccountArchiveService accounts) : LoadableViewModel
{
    public string[] Types { get; } = ["All types", "Account", "Code", "Gym plan", "Amenity"];
    public string[] Statuses { get; } = ["All statuses", "Inactive", "Deleted", "Expired"];
    public ObservableCollection<ArchiveRecord> Rows { get; } = new();
    [ObservableProperty] private int typeIndex;
    [ObservableProperty] private int statusIndex;
    [ObservableProperty] private bool useDates;
    [ObservableProperty] private DateTime from = DateTime.Today.AddMonths(-1);
    [ObservableProperty] private DateTime to = DateTime.Today;
    [ObservableProperty] private string reason = "";
    [ObservableProperty] private bool canManage;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private ArchiveRecord? selected;
    [ObservableProperty] private bool confirmingRestore;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PageText))] private int page;
    [ObservableProperty] private bool hasMore;
    public string PageText => $"Page {Page + 1}";
    ArchiveFilter _applied = new();

    public override async Task OnAppearingAsync()
    {
        if (IsBusy) return;
        var access = await accounts.CanManageAsync();
        CanManage = access.Ok && access.Value;
        if (!CanManage) { Rows.Clear(); IsEmpty = false; Selected = null; ErrorMessage = "Only an active admin can view the archive."; return; }
        await LoadPageAsync(Page);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy || !CanManage) return;
        if (UseDates && To.Date < From.Date) { ErrorMessage = "The end date must be on or after the start date."; return; }
        _applied = new(TypeIndex switch { 1 => "account", 2 => "code", 3 => "plan", 4 => "amenity", _ => null },
            StatusIndex switch { 1 => "inactive", 2 => "deleted", 3 => "expired", _ => null },
            UseDates ? DateOnly.FromDateTime(From) : null, UseDates ? DateOnly.FromDateTime(To) : null, Reason);
        await LoadPageAsync(0);
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        if (IsBusy) return;
        TypeIndex = StatusIndex = 0; UseDates = false; Reason = "";
        From = DateTime.Today.AddMonths(-1); To = DateTime.Today; _applied = new();
        if (CanManage) await LoadPageAsync(0);
    }

    async Task LoadPageAsync(int target)
    {
        Selected = null; ConfirmingRestore = false; IsEmpty = false;
        if (!await RunAsync(() => service.ListAsync(_applied, target), result =>
        { Fill(Rows, result.Rows); Page = target; HasMore = result.HasMore; IsEmpty = Rows.Count == 0; }))
        { Rows.Clear(); HasMore = false; }
    }

    [RelayCommand] private Task PreviousAsync() => !IsBusy && Page > 0 ? LoadPageAsync(Page - 1) : Task.CompletedTask;
    [RelayCommand] private Task NextAsync() => !IsBusy && HasMore ? LoadPageAsync(Page + 1) : Task.CompletedTask;
    [RelayCommand] private void View(ArchiveRecord record) { if (IsBusy) return; Selected = record; ConfirmingRestore = false; Notice = null; }
    [RelayCommand] private void CloseDetails() { if (!IsBusy) { Selected = null; ConfirmingRestore = false; } }
    [RelayCommand] private void RequestRestore() { if (!IsBusy && CanManage && Selected?.CanRestore == true) ConfirmingRestore = true; }
    [RelayCommand] private void CancelRestore() { if (!IsBusy) ConfirmingRestore = false; }

    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        if (IsBusy || !CanManage || !ConfirmingRestore || Selected is not { CanRestore: true } row) return;
        if (!await RunAsync(() => service.RestoreAsync(row))) return;
        ConfirmingRestore = false;
        await LoadPageAsync(0);
        Notice = $"{row.Name} restored.";
    }
}
