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

/// <summary>G14: one coach in the sheet's list; the picked one is outlined.</summary>
public partial class CoachChoice(FreeCoach coach) : ObservableObject
{
    public FreeCoach Coach { get; } = coach;
    public string Name => Coach.Name;
    [ObservableProperty] private bool isPicked;
}

/// <summary>
/// Members: Book an amenity (F12). The amenity cards; Book opens a sheet with the month, the start times still free that day, how long and how to pay.
/// A member with a pass or a session books straight away; anyone else holds the time until the payment is approved. Below, their bookings with
/// Cancel, End now, Send proof and "I didn't use it" (F13).
/// </summary>
public partial class BookAmenityViewModel(IReservationService svc, IAmenityService amenities, IAppNavigator nav, IMembershipService? membership = null) : LoadableViewModel
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));   // Manila
    const int DaysAhead = 14;

    public ObservableCollection<AmenityCard> Cards { get; } = new();
    public ObservableCollection<BookingRow> Bookings { get; } = new();
    public ObservableCollection<BookDay> Days { get; } = new();
    public ObservableCollection<PickChip> Times { get; } = new();
    public ObservableCollection<PickChip> Lengths { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPicking), nameof(SheetTitle), nameof(SheetCaption), nameof(PayFirst), nameof(BookText), nameof(HoldText), nameof(Split), nameof(CoachFeeText))]
    private AmenityCard? picked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MonthText))] private DateOnly month = new(Today.Year, Today.Month, 1);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayText))] private DateOnly day = Today;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(SummaryDetail), nameof(CanBook), nameof(CoachesTitle))] private PickChip? time;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(SummaryDetail), nameof(CanBook), nameof(CoachesTitle))] private PickChip? length;

    // ---- G14, G13, G4 (0064, approved mockup docs/mockups/amenity-down-payment.html): who, the coach, a code, and the split ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSolo), nameof(CanBook), nameof(Split), nameof(BookText), nameof(HoldText), nameof(SummaryDetail))] private bool isWithCoach;
    public bool IsSolo => !IsWithCoach;
    public ObservableCollection<CoachChoice> Coaches { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanBook), nameof(Split), nameof(BookText), nameof(HoldText), nameof(SummaryDetail))] private FreeCoach? pickedCoach;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Split), nameof(BookText), nameof(SummaryDetail))] private string code = "";
    /// <summary>The member's codes that fit this amenity (G4), as on Packages.</summary>
    public ObservableCollection<AvailableCode> Codes { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Split), nameof(BookText), nameof(SummaryDetail))] private int percent = 50;
    IReadOnlyList<AvailableCode> _myCodes = [];

    public bool HasCoaches => Coaches.Count > 0;
    public bool HasCodes => Codes.Count > 0;
    public string CoachFeeText => Picked is { } p && p.Amenity.CoachFee > 0 ? $"+ ₱{p.Amenity.CoachFee:N2} coach fee" : "No coach fee";
    public string CoachesTitle => Time is null || Length is null ? "Pick a time to see who's free"
        : $"Coaches free {Manila(Time.StartUtc).ToString("h:mm", En)} to {Manila(Time.StartUtc.AddMinutes(Length.Minutes)).ToString("h:mm tt", En)}";
    int CodePercent => Codes.FirstOrDefault(c => string.Equals(c.Code, Code.Trim(), StringComparison.OrdinalIgnoreCase))?.Percent ?? 0;
    /// <summary>What booking now costs: with a pass only the coach fee, paid at the desk; otherwise the down payment now and the balance at the desk.</summary>
    public PriceSplit Split => Picked is not { } p ? new(0, 0, 0, 0, 0)
        : PayFirst ? BookingPrice.Split(p.Amenity.Price, p.Amenity.CoachFee, IsWithCoach && PickedCoach is not null, Percent, CodePercent)
        : BookingPrice.Split(0, p.Amenity.CoachFee, IsWithCoach && PickedCoach is not null, 0);
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
    public string BookText => PayFirst ? $"Book and pay ₱{Split.Down:N2}" : "Book";
    public bool CanBook => Time is not null && Length is not null && (!IsWithCoach || PickedCoach is not null);
    public string HoldText => PayFirst ? "Not yours until the desk verifies the down payment. If someone else's is verified first, yours is refunded."
        : Split.Balance > 0 ? $"Uses your pass. The ₱{Split.Balance:N2} coach fee is paid at the desk." : "Uses your pass, nothing to pay.";
    /// <summary>Why times are faded: none left that day, or (paying first, today) the hold needs the start more than 1 hour away.</summary>
    public string TimesNote => !Times.Any(t => t.CanPick) ? (PayFirst ? "No free times this day. Online bookings start more than 1 hour from now." : "No free times this day.")
        : PayFirst && Day == Today ? "Times less than 1 hour from now can't be held online." : "";

    public string Summary => Time is null || Length is null ? "Pick a start time"
        : $"{Manila(Time.StartUtc).ToString("ddd d MMM, h:mm", En)} to {Manila(Time.StartUtc.AddMinutes(Length.Minutes)).ToString("h:mm tt", En)}";
    public string SummaryDetail => Picked is null || Time is null ? "" : PayFirst
        ? $"Total ₱{Split.Due:N2}{(Split.Cut > 0 ? $" after ₱{Split.Cut:N2} off" : "")}. Down payment now ({Percent}%) ₱{Split.Down:N2}, balance at the desk ₱{Split.Balance:N2}."
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
        (IsWithCoach, PickedCoach, Code) = (false, null, "");
        Coaches.Clear(); OnPropertyChanged(nameof(HasCoaches));
        if (await svc.DownPaymentPercentAsync() is { Ok: true } pct) Percent = pct.Value;
        if (membership is not null && await membership.AvailableCodesAsync() is { Ok: true, Value: { } codes }) _myCodes = codes;
        Fill(Codes, _myCodes.Where(c => c.AllAmenities || c.AmenityIds.Contains(card.Amenity.Id)));
        OnPropertyChanged(nameof(HasCodes));
        await PickDayAsync(Today);
        if (!Times.Any(t => t.CanPick)) await PickDayAsync(Today.AddDays(1));   // late in the day: start on tomorrow
    }

    [RelayCommand] private void Close() { Picked = null; Time = null; Length = null; Times.Clear(); Lengths.Clear(); Coaches.Clear(); PickedCoach = null; }

    [RelayCommand] private void Solo() { IsWithCoach = false; PickedCoach = null; }
    [RelayCommand] private Task WithCoach() { IsWithCoach = true; return LoadCoachesAsync(); }
    [RelayCommand]
    private void PickCoach(CoachChoice choice)
    {
        foreach (var c in Coaches) c.IsPicked = c == choice;
        PickedCoach = choice.Coach;
    }
    [RelayCommand] private void UseCode(AvailableCode c) => Code = c.Code;

    /// <summary>G14: who is free for the picked time; a coach picked earlier stays only if still free.</summary>
    [RelayCommand]
    private async Task LoadCoachesAsync()
    {
        if (!IsWithCoach || Picked is not { } card || Time is null || Length is null) { Coaches.Clear(); OnPropertyChanged(nameof(HasCoaches)); return; }
        if (!await RunAsync(() => svc.FreeCoachesAsync(card.Amenity.Id, Time.StartUtc, Length.Minutes),
                list => Fill(Coaches, list.Select(f => new CoachChoice(f) { IsPicked = f.CoachId == PickedCoach?.CoachId })))) return;
        if (PickedCoach is { } c && Coaches.All(x => x.Coach.CoachId != c.CoachId)) PickedCoach = null;
        OnPropertyChanged(nameof(HasCoaches));
    }

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
        if (IsWithCoach) _ = LoadCoachesAsync();
    }

    [RelayCommand] private void PickMethod(string value) => Method = value;

    [RelayCommand]
    private async Task BookAsync()
    {
        if (Picked is not { } card || Time is null || Length is null) return;
        Notice = null;
        var how = !PayFirst ? HoldText : (PayCash ? $"Pay the ₱{Split.Down:N2} down payment at the front desk." : $"Pay the ₱{Split.Down:N2} down payment by GCash and send your proof.")
            + $" The ₱{Split.Balance:N2} balance is paid at the desk. It's yours once the desk verifies the down payment.";
        if (!await Confirm.AskAsync($"Book {card.Amenity.Name}?", $"{Summary}\n{how}", BookText)) return;
        Reservation? made = null;
        if (!await RunAsync(() => svc.BookAsync(card.Amenity.Id, Time.StartUtc, Length.Minutes, Method, IsWithCoach ? PickedCoach?.CoachId : null, Code), r => made = r))
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
            ? $"Requested. {row?.Detail ?? "Pay the down payment at the front desk."}"
            : "Requested. Send your GCash proof for the down payment from My membership. It's yours once the desk verifies it.";
    }

    // ---------- my bookings ----------
    [RelayCommand]
    private async Task CancelAsync(BookingRow row)
    {
        Notice = null;
        var refund = row.Booking.StartsAt.ToUniversalTime().AddHours(-24) >= DateTime.UtcNow;
        var body = row.IsWaiting ? "The request and its payment are withdrawn." : row.Paid
            ? refund ? $"The ₱{row.Payment!.Amount:N2} down payment will be repaid at the front desk." : $"It starts within 24 hours, so the gym keeps the ₱{row.Payment!.Amount:N2} down payment."
            : row.When;
        if (!await Confirm.AskAsync($"Cancel your {row.Amenity} booking?", body, "Cancel booking")) return;
        if (!await RunAsync(() => svc.CancelAsync(row.Booking.Id))) return;
        Notice = row.Paid && refund ? "Booking cancelled. Your down payment refund is approved; collect it at the front desk." : "Booking cancelled.";
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
