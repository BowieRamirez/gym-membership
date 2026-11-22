using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

/// <summary>A day on the booking sheet's month, in the coach Dashboard's style: today on yellow, the chosen day outlined, days that can't be booked dimmed.</summary>
public sealed record BookDay(DateOnly Date, bool InMonth, bool IsToday, bool IsSelected, bool CanPick, bool HasBooking, string? ClosedReason = null)
{
    public string Number => Date.Day.ToString(CultureInfo.InvariantCulture);
    public bool Dimmed => (!CanPick && !IsClosed) || !InMonth;
    /// <summary>G16 (0065): closed days stay readable, crossed out in red, and can be opened to read why.</summary>
    public bool IsClosed => ClosedReason is not null;
    public string Description => Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture) + (IsToday ? ", today" : "") + (HasBooking ? ", you have a booking" : "") + (ClosedReason is { } why ? $", gym closed: {why}" : CanPick ? "" : ", can't be booked");   // G16 (0065)
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
public partial class BookAmenityViewModel(IReservationService svc, IAmenityService amenities, IAppNavigator nav, IMembershipService? membership = null, IUrlOpener? opener = null) : LoadableViewModel
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));   // Manila
    const int DaysAhead = 14;

    public ObservableCollection<AmenityCard> Cards { get; } = new();
    public ObservableCollection<BookingRow> Bookings { get; } = new();
    public ObservableCollection<BookDay> Days { get; } = new();
    public ObservableCollection<PickChip> Times { get; } = new();
    public ObservableCollection<PickChip> Lengths { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPicking), nameof(SheetTitle), nameof(SheetCaption), nameof(PayFirst), nameof(BookText), nameof(HoldText), nameof(Split))]
    private AmenityCard? picked;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(MonthText))] private DateOnly month = new(Today.Year, Today.Month, 1);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DayText), nameof(IsDayClosed))] private DateOnly day = Today;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(SummaryDetail), nameof(CanBook))] private PickChip? time;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary), nameof(SummaryDetail), nameof(CanBook), nameof(Split), nameof(BookText), nameof(CodeCheckText))] private PickChip? length;

    // ---- G13, G4 (0064): a code and the split (0118: amenity price only, no coach fee) ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Split), nameof(BookText), nameof(SummaryDetail))] private string code = "";
    // owner fixes 2026-11-19 Q12, Q13 (0107): live code check as it's typed (500 ms debounce) or on unfocus
    public TimeSpan CodeCheckDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CodeCheckText), nameof(CodeWorks), nameof(HasCodeCheck), nameof(Split), nameof(BookText), nameof(SummaryDetail))] private CodeCheck? codeCheck;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CodeCheckText), nameof(HasCodeCheck))] private string? codeCheckError;
    public bool CodeWorks => CodeCheck is { Works: true };
    public string CodeCheckText => !string.IsNullOrWhiteSpace(CodeCheckError) ? CodeCheckError
        : CodeCheckFeedback.Message(CodeCheck, "amenity", CodeCheck is { Works: true } ? Split.Due : null);
    public bool HasCodeCheck => !string.IsNullOrWhiteSpace(CodeCheckText);
    CancellationTokenSource? _codeCheck;
    (int ItemId, string Code)? _checked;
    partial void OnCodeChanged(string value) => _ = ScheduleCodeCheckAsync(value, immediate: false);
    public Task CheckCodeNowAsync() => ScheduleCodeCheckAsync(Code, immediate: true);
    void ClearCodeCheck() { _codeCheck?.Cancel(); _codeCheck = null; _checked = null; CodeCheck = null; CodeCheckError = null; }
    async Task ScheduleCodeCheckAsync(string? raw, bool immediate)
    {
        var trimmed = raw?.Trim() ?? "";
        if (immediate && Picked is { } cur && _checked == (cur.Amenity.Id, trimmed)) return;
        _codeCheck?.Cancel();
        _checked = null;
        CodeCheck = null; CodeCheckError = null;
        if (membership is null || Picked is not { } card || !PayFirst || trimmed.Length == 0) return;
        var cts = _codeCheck = new CancellationTokenSource();
        if (!immediate && CodeCheckDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(CodeCheckDelay, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
        var res = await membership.CheckCodeAsync(trimmed, "amenity", card.Amenity.Id);
        if (cts.IsCancellationRequested || Picked?.Amenity.Id != card.Amenity.Id) return;
        _checked = (card.Amenity.Id, trimmed);
        if (!res.Ok) { CodeCheckError = ErrorText.For(res.Error, res.Message); return; }
        CodeCheck = res.Value;
    }
    /// <summary>The member's codes that fit this amenity (G4), as on Packages.</summary>
    public ObservableCollection<AvailableCode> Codes { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Split), nameof(BookText), nameof(SummaryDetail), nameof(PayWithText), nameof(HoldText))] private int percent = 50;
    IReadOnlyList<AvailableCode> _myCodes = [];
    IReadOnlyDictionary<DateOnly, string> _closed = new Dictionary<DateOnly, string>();   // G16 (0065)

    public bool HasCodes => Codes.Count > 0;
    int CodePercent => CodeCheck is { Works: true, Percent: > 0 } ? CodeCheck.Percent.Value
        : Codes.FirstOrDefault(c => string.Equals(c.Code, Code.Trim(), StringComparison.OrdinalIgnoreCase))?.Percent ?? 0;
    /// <summary>What booking now costs: with a pass nothing to pay; otherwise the down payment now and the balance at the desk.</summary>
    public PriceSplit Split => Picked is not { } p ? new(0, 0, 0, 0, 0)
        : PayFirst ? BookingPrice.Split(p.Amenity.Price, 0, false, Percent, CodePercent, Hours)
        : new(0, 0, 0, 0, 0);
    int Hours => Math.Max(1, (Length?.Minutes ?? 60) / 60);   // 0090: per hour
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
    public string MonthText => Month.ToString("MMMM yyyy", En);
    public string DayText => Day.ToString("dddd d MMMM", En);
    public string BookText => PayFirst ? $"Book and pay ₱{Split.Down:N2}" : "Book";
    public bool CanBook => Time is not null && Length is not null;
    /// <summary>Owner, 2026-11-18: at 100% (Gym settings) the whole price is paid when booking, so there is no "down payment" or balance.</summary>
    bool PaysInFull => Percent >= 100;
    string PayWord => PaysInFull ? "payment" : "down payment";
    public string PayWithText => $"Pay the {PayWord} online: GCash or QR Ph, through Xendit. To pay cash, book at the front desk.";   // 0099: members pay online
    public string HoldText => PayFirst ? $"Not yours until the {PayWord} is paid online. If someone else's is paid first, yours is refunded."
        : Split.Balance > 0 ? $"Uses your pass. The ₱{Split.Balance:N2} coach fee is paid at the desk." : "Uses your pass, nothing to pay.";
    /// <summary>Why times are faded: none left that day, or (paying first, today) the hold needs the start more than 1 hour away.</summary>
    public bool IsDayClosed => _closed.ContainsKey(Day);
    public string TimesNote => _closed.TryGetValue(Day, out var why) ? $"The gym is closed this day: {why}."
        : !Times.Any(t => t.CanPick) ? (PayFirst ? "No free times this day. Online bookings start more than 1 hour from now." : "No free times this day.")
        : PayFirst && Day == Today ? "Times less than 1 hour from now can't be held online." : "";

    public string Summary => Time is null || Length is null ? "Pick a start time"
        : $"{Manila(Time.StartUtc).ToString("ddd d MMM, h:mm", En)} to {Manila(Time.StartUtc.AddMinutes(Length.Minutes)).ToString("h:mm tt", En)}";
    public string SummaryDetail => Picked is null || Time is null ? "" : PayFirst
        ? $"Total ₱{Split.Due:N2}{(Split.Cut > 0 ? $" after ₱{Split.Cut:N2} off" : "")}. {(PaysInFull ? "Paid in full now." : $"Down payment now ({Percent}%) ₱{Split.Down:N2}, balance at the desk ₱{Split.Balance:N2}.")}"
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
        Notice = null; ErrorMessage = null; ClearCodeCheck();
        Picked = card; Month = new(Today.Year, Today.Month, 1);
        Code = "";
        var pctTask = svc.DownPaymentPercentAsync();
        var closedTask = svc.ClosedDaysAsync();
        var codesTask = membership is not null ? membership.AvailableCodesAsync() : null;
        await (codesTask is not null ? Task.WhenAll(pctTask, closedTask, codesTask) : Task.WhenAll(pctTask, closedTask));
        if (pctTask.Result is { Ok: true } pct) Percent = pct.Value;
        if (closedTask.Result is { Ok: true, Value: { } closed }) _closed = closed;
        if (codesTask?.Result is { Ok: true, Value: { } codes }) _myCodes = codes;
        Fill(Codes, _myCodes.Where(c => c.AllAmenities || c.AmenityIds.Contains(card.Amenity.Id)));
        OnPropertyChanged(nameof(HasCodes));
        await PickDayAsync(Today);
        if (!Times.Any(t => t.CanPick)) await PickDayAsync(Today.AddDays(1));   // late in the day: start on tomorrow
    }

    [RelayCommand] private void Close() { ClearCodeCheck(); Picked = null; Time = null; Length = null; Times.Clear(); Lengths.Clear(); }
    [RelayCommand] private void UseCode(AvailableCode c) { Code = c.Code; _ = CheckCodeNowAsync(); }

    [RelayCommand] private void PreviousMonth() { if (Month > new DateOnly(Today.Year, Today.Month, 1)) { Month = Month.AddMonths(-1); BuildDays(); } }
    [RelayCommand] private void NextMonth() { var last = Today.AddDays(DaysAhead); if (Month < new DateOnly(last.Year, last.Month, 1)) { Month = Month.AddMonths(1); BuildDays(); } }

    [RelayCommand] private Task PickDay(BookDay d) => d.CanPick || d.IsClosed ? PickDayAsync(d.Date) : Task.CompletedTask;

    async Task PickDayAsync(DateOnly d)
    {
        Day = d; Time = null; Length = null;
        BuildDays();
        if (_closed.ContainsKey(d)) { Times.Clear(); Lengths.Clear(); OnPropertyChanged(nameof(TimesNote)); return; }
        if (!await RunAsync(() => svc.GridAsync(d), g => _grid = g)) return;
        BuildTimes();
    }

    void BuildDays()
    {
        var mine = Bookings.Where(b => b.Booking.Status is "confirmed" or "awaiting_payment").Select(b => DateOnly.FromDateTime(Manila(b.Booking.StartsAt))).ToHashSet();
        Fill(Days, AttendanceCalendar.MonthGrid(Month.Year, Month.Month).Select(x =>
            new BookDay(x, x.Month == Month.Month, x == Today, x == Day, x >= Today && x <= Today.AddDays(DaysAhead) && !_closed.ContainsKey(x), mine.Contains(x),
                _closed.GetValueOrDefault(x))));
    }

    void BuildTimes()
    {
        Times.Clear(); Lengths.Clear();
        if (_grid is null || Picked is null) return;
        foreach (var t in BookingSlots.HourStarts(_grid))
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
        Lengths.Clear();   // 0090: a dropdown of whole hours that fit, up to closing time
        foreach (var m in fits) Lengths.Add(new PickChip(BookingSlots.LengthText(m), true, minutes: m));
        Time = chip;
        Length = Lengths.OrderBy(l => Math.Abs(l.Minutes - keep)).First();
    }

    [RelayCommand] private void PickLength(PickChip chip) => Length = chip;
    partial void OnLengthChanged(PickChip? value)
    {
        foreach (var c in Lengths) c.IsPicked = c == value;
    }

    [RelayCommand]
    private async Task BookAsync()
    {
        if (Picked is not { } card || Time is null || Length is null) return;
        Notice = null;
        var how = !PayFirst ? HoldText
            : PaysInFull ? $"Pay ₱{Split.Down:N2} online now. It's yours once it's paid."
            : $"Pay the ₱{Split.Down:N2} down payment online now."
                + $" The ₱{Split.Balance:N2} balance is paid at the desk. It's yours once the down payment is paid.";
        if (!await Confirm.AskAsync($"Book {card.Amenity.Name}?", $"{Summary}\n{how}", BookText)) return;
        Reservation? made = null;
        if (!await RunAsync(() => svc.BookAsync(card.Amenity.Id, Time.StartUtc, Length.Minutes, PayMethod.Qr, null, Code), r => made = r))
        {
            var error = ErrorMessage;   // nothing was booked: show the day as it is now, with the reason
            await PickDayAsync(Day);
            ErrorMessage = error;
            return;
        }
        Close();
        await LoadAsync();
        Notice = made!.Status != "awaiting_payment" ? "Booked." : $"Requested. Tap Pay online on the booking below. It's yours once the {PayWord} is paid.";
    }

    // ---------- my bookings ----------
    [RelayCommand]
    private async Task CancelAsync(BookingRow row)
    {
        Notice = null;
        var refund = row.Refundable;
        var body = row.IsWaiting ? "The request and its payment are withdrawn." : row.Paid
            ? refund ? $"The {row.PaidWords} will be repaid at the front desk." : $"It starts within 24 hours, so the gym keeps the {row.PaidWords}."
            : row.When;
        if (!await Confirm.AskAsync($"Cancel your {row.Amenity} booking?", body, "Cancel booking")) return;
        if (!await RunAsync(() => svc.CancelAsync(row.Booking.Id))) return;
        Notice = row.Paid && refund ? $"Booking cancelled. Your {(row.Booking.Balance > 0 ? "down payment " : "")}refund is approved; collect it at the front desk." : "Booking cancelled.";
        await LoadBookingsAsync();
    }

    [RelayCommand]
    private async Task EndAsync(BookingRow row)
    { Notice = null; if (!await Confirm.AskAsync($"End your {row.Amenity} booking now?", "The amenity cools down for 30 minutes from now.", "End now")) return; if (await RunAsync(() => svc.EndAsync(row.Booking.Id))) { Notice = "Ended. The amenity cools down for 30 minutes from now."; await LoadBookingsAsync(); } }

    // Xendit's checkout for the down payment; demo mode is paid at once. Coming back to the page reloads the bookings.
    [RelayCommand]
    private async Task PayOnlineAsync(BookingRow row)
    {
        Notice = null;
        if (membership is null || row.Payment is not { } pay) return;
        if (!await Confirm.AskAsync(row.Booking.Balance > 0 ? $"Pay the ₱{pay.Amount:N2} down payment online?" : $"Pay ₱{pay.Amount:N2} online?", $"{row.Amenity}, {row.When}\nYou pay on Xendit's page by GCash or QR Ph, then come back here.", "Pay online")) return;
        OnlineCheckout? checkout = null;
        if (!await RunAsync(() => membership.PayOnlineAsync(pay.Id), c => checkout = c)) return;
        if (checkout!.Paid) { Notice = "Paid."; await LoadBookingsAsync(); return; }
        if (checkout.Url is { } url && opener is not null)
        {
            await opener.OpenAsync(url);
            Notice = "Finish paying on Xendit's page, then come back here.";
        }
    }

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
