using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

/// <summary>One 30-minute cell: an amenity at a time. Only open cells can be selected.</summary>
public partial class SlotCell(int amenityId, DateTime startUtc, string state) : ObservableObject
{
    public int AmenityId { get; } = amenityId;
    public DateTime StartUtc { get; } = startUtc;
    public string State { get; } = state;   // open, yours, booked, cooldown, blocked, closed, past
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Label))] private bool selected;
    public bool CanSelect => State == "open";
    public string Label => Selected ? "Selected" : State switch
    {
        "open" => "Open", "yours" => "Yours", "booked" => "Booked", "cooldown" => "Cooldown",
        "blocked" => "Too close", "closed" => "Closed", _ => "—"
    };
}

public sealed record SlotRow(string Time, IReadOnlyList<SlotCell> Cells);

/// <summary>Members: a day of 30-minute slots, one column per amenity. Pick open cells and book them in one go (all or nothing).</summary>
public partial class BookAmenityViewModel(IReservationService svc) : LoadableViewModel
{
    public ObservableCollection<string> Amenities { get; } = new();
    public ObservableCollection<SlotRow> Rows { get; } = new();
    public ObservableCollection<BookingRow> Bookings { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayText))] private DateTime day = DateTime.UtcNow.AddHours(8).Date;   // a Manila date
    [ObservableProperty, NotifyPropertyChangedFor(nameof(BookText))] private int selectedCount;
    public string DayText => Day.ToString("dddd d MMMM");
    public string BookText => SelectedCount == 0 ? "Pick open slots to book" : $"Book selected slots ({SelectedCount})";

    public override Task OnAppearingAsync() => LoadAsync();
    public override Task AutoRefreshAsync() => IsBusy ? Task.CompletedTask : LoadAsync();

    [RelayCommand] private Task NextDayAsync() { if (Day < DateTime.UtcNow.AddHours(8).Date.AddDays(14)) Day = Day.AddDays(1); return LoadAsync(); }
    [RelayCommand] private Task PreviousDayAsync() { if (Day > DateTime.UtcNow.AddHours(8).Date) Day = Day.AddDays(-1); return LoadAsync(); }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var keep = Selected().Select(c => (c.AmenityId, c.StartUtc)).ToHashSet();   // a refresh keeps picks that are still open
        if (!await RunAsync(() => svc.GridAsync(DateOnly.FromDateTime(Day)), g => Build(g, keep))) return;
        await RunAsync(svc.MyBookingsAsync, rows => Fill(Bookings, rows));
    }

    void Build(AmenityGrid g, HashSet<(int, DateTime)> keep)
    {
        Fill(Amenities, g.Amenities.Select(a => a.Name));
        var rows = new List<SlotRow>();
        for (var t = g.Opens; t < g.Closes; t = t.AddMinutes(30))
        {
            var cells = g.Amenities.Select(a =>
            {
                var cell = new SlotCell(a.Id, t, StateOf(g, a, t));
                cell.Selected = cell.CanSelect && keep.Contains((a.Id, t));
                cell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SlotCell.Selected)) SelectedCount = Selected().Count(); };
                return cell;
            }).ToList();
            rows.Add(new SlotRow(t.AddHours(8).ToString("HH:mm"), cells));
        }
        Fill(Rows, rows);
        SelectedCount = Selected().Count();
    }

    // the same rules the server applies at booking time; it rechecks everything anyway
    static string StateOf(AmenityGrid g, GridAmenity a, DateTime t)
    {
        var end = t.AddMinutes(30);
        var blocks = g.Blocks.Where(b => b.Amenity == a.Id).ToList();
        if (blocks.FirstOrDefault(b => b.Starts < end && t < b.Ends) is { } use) return use.Mine ? "yours" : "booked";
        if (t <= g.Now) return "past";
        if (blocks.Any(b => b.Ends < end && t < b.Cooldown)) return "cooldown";
        if (a.Status == "unavailable" && (a.Back is null || a.Back > t)) return "closed";
        if (blocks.Any(b => b.Starts >= end && b.Starts < end.AddMinutes(30))) return "blocked";   // its own cooldown would run into the next booking
        return "open";
    }

    IEnumerable<SlotCell> Selected() => Rows.SelectMany(r => r.Cells).Where(c => c.Selected);

    [RelayCommand] private void Toggle(SlotCell cell) { if (cell.CanSelect) cell.Selected = !cell.Selected; }

    [RelayCommand]
    private async Task BookAsync()
    {
        Notice = null;
        var picks = Selected().Select(c => (c.AmenityId, c.StartUtc)).ToList();
        if (picks.Count == 0) return;
        var booked = 0;
        if (!await RunAsync(() => svc.BookAsync(picks), n => booked = n))
        {
            var error = ErrorMessage;   // nothing was booked; show the grid as it is now, with the reason
            await LoadAsync();
            ErrorMessage = error;
            return;
        }
        foreach (var c in Selected()) c.Selected = false;
        await LoadAsync();
        Notice = booked == 1 ? "Booked." : $"Booked {booked} sessions.";
    }

    [RelayCommand]
    private async Task CancelAsync(BookingRow row)
    { Notice = null; if (await RunAsync(() => svc.CancelAsync(row.Booking.Id))) { Notice = "Booking cancelled."; await LoadAsync(); } }

    [RelayCommand]
    private async Task EndAsync(BookingRow row)
    { Notice = null; if (await RunAsync(() => svc.EndAsync(row.Booking.Id))) { Notice = "Ended. The amenity cools down for 30 minutes from now."; await LoadAsync(); } }
}
