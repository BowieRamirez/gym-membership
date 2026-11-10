using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

/// <summary>A day on the booking sheet's month, in the coach Dashboard's style: today on yellow, the chosen day outlined, days that can't be booked dimmed.</summary>
public sealed record BookDay(DateOnly Date, bool InMonth, bool IsToday, bool IsSelected, bool CanPick, bool HasBooking)
{
    public string Number => Date.Day.ToString(CultureInfo.InvariantCulture);
    public bool Dimmed => !CanPick || !InMonth;
    public string Description => Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture) + (IsToday ? ", today" : "") + (HasBooking ? ", you have a booking" : "") + (CanPick ? "" : ", can't be booked");
}

/// <summary>A start time or a length on the sheet; only ones that fit can be picked.</summary>
public partial class PickChip(string text, bool canPick, DateTime start = default, int minutes = 0) : ObservableObject
{
    public string Text { get; } = text;
    public bool CanPick { get; } = canPick;
    public DateTime StartUtc { get; } = start;
    public int Minutes { get; } = minutes;
    [ObservableProperty] private bool isPicked;
}

/// <summary>
/// Members: Book an amenity (F12). The amenity cards; Book opens a sheet with the month, the start times still free that day, how long and how to pay.
/// A member with a pass or a session books straight away; anyone else holds the time until the payment is approved. Below, their bookings with
/// Cancel, End now, Send proof and "I didn't use it" (F13).
/// </summary>
public partial class BookAmenityViewModel(IReservationService svc, IAmenityService amenities, IAppNavigator nav) : LoadableViewModel
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));   // Manila
    const int DaysAhead = 14;

    public ObservableCollection<AmenityCard> Cards { get; } = new();
    public ObservableCollection<BookingRow> Bookings { get; } = new();
    public ObservableCollection<BookDay> Days { get; } = new();
    public ObservableCollection<PickChip> Times { get; } = new();
    public ObservableCollection<PickChip> Lengths { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPicking), nameof(SheetTitle), nameof(SheetCaption), nameof(PayFirst), nameof(BookText), nameof(HoldText))]
    private AmenityCard? picked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MonthText))] private DateOnly month = new(Today.Year, Today.Month, 1);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayText))] private DateOnly day = Today;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(SummaryDetail), nameof(CanBook))] private PickChip? time;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(SummaryDetail), nameof(CanBook))] private PickChip? length;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PayQr), nameof(PayCash))] private string method = PayMethod.Qr;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsClaiming))] private BookingRow? claiming;
    [ObservableProperty] private string claimReason = "";
    AmenityGrid? _grid;

    public bool IsPicking => Picked is not null;
    public bool IsClaiming => Claiming is not null;
    public bool HasBookings => Bookings.Count > 0;
    public string SheetTitle => Picked is null ? "" : $"Book {Picked.Amenity.Name}";
    public string SheetCaption => Picked is null ? "" : $"{Picked.PassText} · {Picked.RunText}";
    /// <summary>No pass or session for this amenity: the booking is held until the payment is approved, and must start more than 1 hour from now.</summary>
    public bool PayFirst => Picked is { HasPass: false };
    public bool PayQr => Method == PayMethod.Qr;
    public bool PayCash => Method == PayMethod.Cash;
    public string MonthText => Month.ToString("MMMM yyyy", En);
    public string DayText => Day.ToString("dddd d MMMM", En);
    public string BookText => PayFirst ? "Book and pay" : "Book";
    public bool CanBook => Time is not null && Length is not null;
    public string HoldText => PayFirst ? "Held for you until paid, up to 1 hour before it starts." : "Uses your pass, nothing to pay.";
    /// <summary>Why times are faded: none left that day, or (paying first, today) the hold needs the start more than 1 hour away.</summary>
    public string TimesNote => !Times.Any(t => t.CanPick) ? (PayFirst ? "No free times this day. Online bookings start more than 1 hour from now." : "No free times this day.")
        : PayFirst && Day == Today ? "Times less than 1 hour from now can't be held online." : "";

    public string Summary => Time is null || Length is null ? "Pick a start time"
        : $"{Manila(Time.StartUtc).ToString("ddd d MMM, h:mm", En)} to {Manila(Time.StartUtc.AddMinutes(Length.Minutes)).ToString("h:mm tt", En)}";
    public string SummaryDetail => Picked is null || Time is null ? "" : PayFirst
        ? $"₱{Picked.Amenity.Price:N2} · {Picked.Amenity.Name} cools down for 30 minutes after."
        : $"With your pass · {Picked.Amenity.Name} cools down for 30 minutes after.";

    static DateTime Manila(DateTime utc) => utc.ToUniversalTime().AddHours(8);

    public override Task OnAppearingAsync() => LoadAsync();
    public override Task AutoRefreshAsync() => IsBusy || IsPicking || IsClaiming ? Task.CompletedTask : LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!await RunAsync(amenities.BoardAsync, b => Fill(Cards, b.Cards))) return;
        await LoadBookingsAsync();
    }

    async Task LoadBookingsAsync()
    {
        await RunAsync(svc.MyBookingsAsync, rows => Fill(Bookings, rows));
        OnPropertyChanged(nameof(HasBookings));
    }

    [RelayCommand]
    private async Task ToggleFollowAsync(AmenityCard card)
    { if (await RunAsync(() => amenities.SetFollowedAsync(card.Amenity.Id, !card.Followed))) await LoadAsync(); }

    // ---------- the sheet ----------
    [RelayCommand]
    private async Task OpenAsync(AmenityCard card)
    {
        Notice = null; ErrorMessage = null;
        Picked = card; Method = PayMethod.Qr; Month = new(Today.Year, Today.Month, 1);
        await PickDayAsync(Today);
        if (!Times.Any(t => t.CanPick)) await PickDayAsync(Today.AddDays(1));   // late in the day: start on tomorrow
    }

    [RelayCommand] private void Close() { Picked = null; Time = null; Length = null; Times.Clear(); Lengths.Clear(); }

    [RelayCommand] private void PreviousMonth() { if (Month > new DateOnly(Today.Year, Today.Month, 1)) { Month = Month.AddMonths(-1); BuildDays(); } }
    [RelayCommand] private void NextMonth() { var last = Today.AddDays(DaysAhead); if (Month < new DateOnly(last.Year, last.Month, 1)) { Month = Month.AddMonths(1); BuildDays(); } }

    [RelayCommand] private Task PickDay(BookDay d) => d.CanPick ? PickDayAsync(d.Date) : Task.CompletedTask;

    async Task PickDayAsync(DateOnly d)
    {
        Day = d; Time = null; Length = null;
        BuildDays();
        if (!await RunAsync(() => svc.GridAsync(d), g => _grid = g)) return;
        BuildTimes();
    }

    void BuildDays()
    {
        var mine = Bookings.Where(b => b.Booking.Status is "confirmed" or "awaiting_payment").Select(b => DateOnly.FromDateTime(Manila(b.Booking.StartsAt))).ToHashSet();
        Fill(Days, AttendanceCalendar.MonthGrid(Month.Year, Month.Month).Select(x =>
            new BookDay(x, x.Month == Month.Month, x == Today, x == Day, x >= Today && x <= Today.AddDays(DaysAhead), mine.Contains(x))));
    }

    void BuildTimes()
    {
        Times.Clear(); Lengths.Clear();
        if (_grid is null || Picked is null) return;
        for (var t = _grid.Opens; t < _grid.Closes; t = t.AddMinutes(30))
            Times.Add(new PickChip(Manila(t).ToString("h:mm tt", En), BookingSlots.Lengths(_grid, Picked.Amenity.Id, t, PayFirst).Count > 0, t));
        OnPropertyChanged(nameof(TimesNote));
    }

    [RelayCommand]
    private void PickTime(PickChip chip)
    {
        if (!chip.CanPick || _grid is null || Picked is null) return;
        foreach (var c in Times) c.IsPicked = c == chip;
        var fits = BookingSlots.Lengths(_grid, Picked.Amenity.Id, chip.StartUtc, PayFirst);
        var keep = Length?.Minutes ?? 60;
        Lengths.Clear();
        foreach (var m in BookingSlots.Minutes) Lengths.Add(new PickChip(m switch { 30 => "30 min", 60 => "1 hour", 90 => "1½ hours", _ => "2 hours" }, fits.Contains(m), minutes: m));
        Time = chip;
        PickLength(Lengths.Where(l => l.CanPick).OrderBy(l => Math.Abs(l.Minutes - keep)).First());
    }

    [RelayCommand]
    private void PickLength(PickChip chip)
    {
        if (!chip.CanPick) return;
        foreach (var c in Lengths) c.IsPicked = c == chip;
        Length = chip;
    }

    [RelayCommand] private void PickMethod(string value) => Method = value;

    [RelayCommand]
    private async Task BookAsync()
    {
        if (Picked is not { } card || Time is null || Length is null) return;
        Notice = null;
        var how = !PayFirst ? "Uses your pass." : PayCash ? $"Pay ₱{card.Amenity.Price:N2} at the front desk before the hold ends." : $"Pay ₱{card.Amenity.Price:N2} by GCash or QR and send your proof.";
        if (!await Confirm.AskAsync($"Book {card.Amenity.Name}?", $"{Summary}\n{how}", BookText)) return;
        Reservation? made = null;
        if (!await RunAsync(() => svc.BookAsync(card.Amenity.Id, Time.StartUtc, Length.Minutes, Method), r => made = r))
        {
            var error = ErrorMessage;   // nothing was booked: show the day as it is now, with the reason
            await PickDayAsync(Day);
            ErrorMessage = error;
            return;
        }
        Close();
        await LoadAsync();
        var row = Bookings.FirstOrDefault(b => b.Booking.Id == made!.Id);
        Notice = made!.Status != "awaiting_payment" ? "Booked." : PayCash
            ? $"Held for you. {row?.Detail ?? "Pay at the front desk before the hold ends."}"
            : "Held for you. Send your GCash or QR proof from My membership before the hold ends.";
    }

    // ---------- my bookings ----------
    [RelayCommand]
    private async Task CancelAsync(BookingRow row)
    {
        Notice = null;
        var body = row.IsWaiting ? "The time is released and the payment withdrawn." : row.Paid ? $"₱{row.Payment!.Amount:N2} will be repaid at the front desk." : row.When;
        if (!await Confirm.AskAsync($"Cancel your {row.Amenity} booking?", body, "Cancel booking")) return;
        if (!await RunAsync(() => svc.CancelAsync(row.Booking.Id))) return;
        Notice = row.Paid ? "Booking cancelled. Your refund is approved; collect it at the front desk." : "Booking cancelled.";
        await LoadBookingsAsync();
    }

    [RelayCommand]
    private async Task EndAsync(BookingRow row)
    { Notice = null; if (!await Confirm.AskAsync($"End your {row.Amenity} booking now?", "The amenity cools down for 30 minutes from now.", "End now")) return; if (await RunAsync(() => svc.EndAsync(row.Booking.Id))) { Notice = "Ended. The amenity cools down for 30 minutes from now."; await LoadBookingsAsync(); } }

    [RelayCommand] private void SendProof() => nav.GoToMembership();

    [RelayCommand] private void Claim(BookingRow row) { Notice = null; Claiming = row; ClaimReason = ""; }
    [RelayCommand] private void KeepClaim() => Claiming = null;

    [RelayCommand]
    private async Task SendClaimAsync()
    {
        if (Claiming is not { } row) return;
        if (!await RunAsync(() => svc.ClaimAsync(row.Booking.Id, ClaimReason))) return;
        Claiming = null;
        Notice = "Sent to the front desk. You'll get a notification when they've checked it.";
        await LoadBookingsAsync();
    }
}
