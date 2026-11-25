using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

/// <summary>0165 (owner, 2026-11-25): a coach reports damage any time, for an amenity on sale or for something else
/// (equipment, a room), and follows what the admins did with each report.</summary>
public partial class ReportDamageViewModel(ISessionService svc) : LoadableViewModel
{
    public const string SomethingElse = "Something else (equipment, a room)";
    public sealed record Place(int? AmenityId, string Name);
    public sealed record Sent(string When, string Where, string What, string Status, string? Note)
    {
        public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    }

    public ObservableCollection<Place> Places { get; } = new();
    public ObservableCollection<Sent> Reports { get; } = new();
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand))] private Place? where;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand)), NotifyPropertyChangedFor(nameof(CountText))] private string description = "";
    public string CountText => $"{Description.Trim().Length} / 1000";
    public bool HasReports => Reports.Count > 0;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private Task LoadAsync() => RunAsync(svc.MyDamageReportsAsync, data =>
    {
        var keep = Where?.AmenityId;
        var names = data.Amenities.ToDictionary(a => a.Id, a => a.Name);
        Fill(Places, data.Amenities.Where(a => a.State == CatalogState.Published).OrderBy(a => a.Name).Select(a => new Place(a.Id, a.Name))
            .Append(new Place(null, SomethingElse)));
        Where = Places.FirstOrDefault(p => p.AmenityId == keep) ?? (keep is null && Where is not null ? Places[^1] : null);
        Fill(Reports, data.Reports.Select(r => new Sent(
            ManilaTime.Of(r.CreatedAt).ToString("ddd d MMM, h:mm tt", System.Globalization.CultureInfo.InvariantCulture),
            r.AmenityId is { } id && names.TryGetValue(id, out var n) ? n : "Something else",
            r.Description,
            r.Status switch { "closed" => "Closed", "reviewed" => "Reviewed", _ => "Open" },
            r.ReviewNote)));
        OnPropertyChanged(nameof(HasReports));
    });

    bool CanSend => Where is not null && Description.Trim().Length is > 0 and <= 1000;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        Notice = null;
        if (!await RunAsync(() => svc.ReportDamageAsync(null, Where!.AmenityId, Description), _ => { })) return;
        Description = "";
        Notice = "Damage report sent. The admins were told.";
        await LoadAsync();
    }
}
