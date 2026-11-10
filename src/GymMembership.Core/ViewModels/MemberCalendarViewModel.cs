using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

/// <summary>
/// Members: Calendar (F15). The month in the coach Dashboard's style with their sessions, amenity bookings and their coach's shifts
/// and open times; the chosen day listed underneath. An open time can be asked for (a time request, same rules); a booking opens
/// Book an amenity. A member without a coach sees their bookings and the gym's hours.
/// </summary>
public partial class MemberCalendarViewModel(IMemberCalendarService svc, ISessionService sessions, IAppNavigator nav) : LoadableViewModel
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));   // Manila

    public ObservableCollection<MemberDay> Days { get; } = new();
    public ObservableCollection<CalendarEntry> DayEntries { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MonthText))] private DateOnly month = new(Today.Year, Today.Month, 1);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayText))] private DateOnly day = Today;
    [ObservableProperty] private bool hasCoach;
    [ObservableProperty] private string hoursText = "";
    IReadOnlyList<CalendarEntry> _entries = [];

    public string MonthText => Month.ToString("MMMM yyyy", En);
    public string DayText => Day.ToString("dddd d MMMM", En);
    public bool DayIsEmpty => DayEntries.Count == 0;

    public override Task OnAppearingAsync() => LoadAsync();
    public override Task AutoRefreshAsync() => IsBusy ? Task.CompletedTask : LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        var grid = AttendanceCalendar.MonthGrid(Month.Year, Month.Month);
        DateTime Utc(DateOnly d) => DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue).AddHours(-8), DateTimeKind.Utc);
        await RunAsync(() => svc.RangeAsync(Utc(grid[0]), Utc(grid[^1].AddDays(1))), data =>
        {
            _entries = data.Entries; HasCoach = data.HasCoach;
            HoursText = $"The gym is open {DateTime.Today.Add(data.Opens).ToString("h:mm tt", En)} to {DateTime.Today.Add(data.Closes).ToString("h:mm tt", En)}."
                + (data.HasCoach ? "" : " Hire a coach to see their shifts and open times here.");
            Build();
        });
    }

    void Build()
    {
        Fill(Days, MemberCalendar.Days(Month, _entries, Today, Day));
        Fill(DayEntries, _entries.Where(e => DateOnly.FromDateTime(e.StartUtc.AddHours(8)) == Day));
        OnPropertyChanged(nameof(DayIsEmpty));
    }

    [RelayCommand] private Task PreviousMonthAsync() { Month = Month.AddMonths(-1); return LoadAsync(); }
    [RelayCommand] private Task NextMonthAsync() { Month = Month.AddMonths(1); return LoadAsync(); }
    [RelayCommand] private Task TodayAsync() { Day = Today; Month = new(Today.Year, Today.Month, 1); return LoadAsync(); }

    [RelayCommand]
    private Task PickDayAsync(MemberDay d)
    {
        Day = d.Date;
        if (!d.InMonth) { Month = new(d.Date.Year, d.Date.Month, 1); return LoadAsync(); }
        Build();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RequestAsync(CalendarEntry open)
    {
        if (!open.CanRequest || open.CoachId is null) return;
        Notice = null;
        if (!await Confirm.AskAsync("Ask for this time?", $"{open.Title}\n{DayText}, {open.Time}", "Send request")) return;
        if (!await RunAsync(() => sessions.RequestAsync(open.CoachId, open.StartUtc, open.EndUtc, null))) return;
        Notice = "Request sent. Your coach answers it in Time requests.";
        await LoadAsync();
    }

    [RelayCommand] private void Open(CalendarEntry entry) { if (entry.CanOpen) nav.GoToBookings(); }
}
