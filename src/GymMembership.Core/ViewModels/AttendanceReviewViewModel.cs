using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

// Owner fixes 2026-11-25, Fix 4 Phase 4.2 (W2, W4, W6, W7): every person's attendance for one pay period (0158). Review lists the
// pending records as cards that say whose they are; Approve shows what it means in money first. Records lists all of them,
// Corrections the period's hour corrections. Approving uses payroll_review_time, the same as the Payroll drawer.

/// <summary>One label / value row of the money table shown before approving.</summary>
public sealed record MoneyRow(string Label, string Value);

/// <summary>One attendance record as the owner's card: whose it is, the day, time in and out, the shift, and its status.</summary>
public sealed partial class AttendanceCardItem(PayrollStaffRecord rec, bool editable, AttendanceReviewViewModel page) : ObservableObject
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string T(DateTime t) => ManilaTime.Of(t).ToString("h:mm tt", Inv);

    public PayrollStaffRecord Rec { get; } = rec;
    public string FullName => Rec.FullName;
    public string Initials => Services.Initials.From(Rec.FullName);
    public string RoleText => Rec.RoleName == "coach" ? "Coach" : "Front Desk";
    public string DateText => ManilaTime.Of(Rec.ActualStart ?? Rec.StartsAt).ToString("MMMM d, yyyy", Inv);
    public string InText => T(Rec.ActualStart ?? Rec.StartsAt);
    public string OutText => Rec.EndsAt is { } e ? T(e) : "Still open";
    public string ShiftText => Rec.ExpectedStart is { } s && Rec.ExpectedEnd is { } e ? $"Shift {T(s)} – {T(e)}" : "";
    public bool HasShift => ShiftText.Length > 0;
    public string WhatText => Rec.Source switch { "training_session" => $"Session · {Rec.Label}", "coach_visit" => "Check-in", _ => Rec.Label };
    public string StatusLabel => Rec.EndsAt is null ? "Open" : Rec.Status switch { "approved" => "Approved", "rejected" => "Rejected", _ => "Pending" };
    public bool IsPending => StatusLabel == "Pending";
    public string FlagText => PayrollRecordItem.FlagWords(Rec.Flags.Where(f => f != "open"));   // the card already says "Still open"
    public bool HasFlags => FlagText.Length > 0;
    public string? Reason => Rec.Reason;
    public bool HasReason => !string.IsNullOrEmpty(Rec.Reason);
    bool CanReview => editable && Rec.EndsAt is not null;
    public bool CanApprove => CanReview && Rec.Status != "approved";
    public bool CanReject => CanReview && Rec.Status != "rejected";

    [ObservableProperty] private bool isRejecting;
    [ObservableProperty] private string rejectReason = "";
    [ObservableProperty] private string? errorText;

    [RelayCommand] private void Approve() { ErrorText = null; page.BeginApprove(this); }
    [RelayCommand] private void BeginReject() { IsRejecting = true; ErrorText = null; }
    [RelayCommand] private void CancelReject() { IsRejecting = false; ErrorText = null; }
    [RelayCommand]
    private async Task RejectAsync()
    {
        var reason = RejectReason.Trim();
        if (reason.Length is 0 or > 200) { ErrorText = "Add a reason (1–200 characters)."; return; }
        ErrorText = await page.ReviewAsync(this, "rejected", reason);
    }

    /// <summary>W4: what approving this record means in money, before deductions.</summary>
    public IReadOnlyList<MoneyRow> MoneyRows()
    {
        var approved = Rec.Status == "approved";
        if (Rec.PayType == "per_session")
            return [new("Recorded", "1 session"), new("Approved", approved ? "1 session" : "0 sessions"),
                    new("Rate", PayTypes.RateText("per_session", Rec.Rate)), new("Potential payable amount", PayTypes.Money(Rec.Rate))];
        string H(decimal h) => $"{h.ToString("0.##", Inv)} hours";
        List<MoneyRow> rows = [new("Recorded hours", H(Rec.Hours)), new("Approved hours", H(approved ? Rec.Hours : 0)),
                new("Hourly rate", PayTypes.Money(Rec.Rate)), new("Potential payable amount", PayTypes.Money(Math.Round(Rec.Hours * Rec.Rate, 2)))];
        // pay merges overlapping time (range_agg, 0142), so this record alone overstates it
        if (Rec.Flags.Contains("overlap")) rows.Add(new("Overlaps another record", "The shared time is paid once, so the period total is lower"));
        return rows;
    }
}

/// <summary>W6: one hour (or session) correction with whose it is; removable while the period is a draft (moved from Payroll, owner option A).</summary>
public sealed class AttendanceCorrectionItem(PayrollHourAdjustment adj, string unit, bool canRemove)
{
    public PayrollHourAdjustment Adj => adj;
    public bool CanRemove => canRemove;
    public string FullName => adj.FullName ?? "";
    public string QuantityText => $"{(adj.Quantity > 0 ? "+" : "")}{adj.Quantity.ToString("0.##", CultureInfo.InvariantCulture)} {unit}";
    public string Note => adj.Note;
    public string ByText => $"{adj.CreatedByName} · {ManilaTime.Of(adj.CreatedAt).ToString("d MMM, h:mm tt", CultureInfo.InvariantCulture)}";
}

public partial class AttendanceReviewViewModel(IAdminService svc) : LoadableViewModel, IOpensTab
{
    List<PayrollStaffRecord> _records = [];

    public ObservableCollection<AttendanceCardItem> Cards { get; } = new();
    public ObservableCollection<AttendanceCorrectionItem> Corrections { get; } = new();
    public ObservableCollection<MoneyRow> MoneyRows { get; } = new();
    public IReadOnlyList<string> StatusOptions { get; } = ["All", "Pending", "Approved", "Rejected"];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(PeriodLabel))] private DateOnly periodStartsOn = PayrollViewModel.PeriodStartFor(PayrollViewModel.ManilaToday());
    [ObservableProperty] private int periodId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReadOnlyText))] private bool isReadOnly;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPending), nameof(PendingText), nameof(ReviewTabText))] private int pendingCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReviewTab), nameof(IsRecordsTab), nameof(IsCorrectionsTab), nameof(EmptyText), nameof(SectionTitle))] private string activeTab = "review";
    // owner, 2026-11-25: no tab bar; the menu opens each part and the page title says which (the menu's own words)
    public string SectionTitle => ActiveTab switch { "records" => "Attendance Records", "corrections" => "Attendance Corrections", _ => "Attendance Review" };
    [ObservableProperty] private string statusFilter = "All";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsConfirming))] private AttendanceCardItem? confirming;
    [ObservableProperty] private string? confirmError;

    public string PeriodLabel => PayrollPdf.PeriodSpan(PeriodStartsOn, PeriodStartsOn.Day == 1 ? PeriodStartsOn.AddDays(14) : PeriodStartsOn.AddMonths(1).AddDays(-PeriodStartsOn.Day));
    public bool IsReviewTab => ActiveTab == "review";
    public bool IsRecordsTab => ActiveTab == "records";
    public bool IsCorrectionsTab => ActiveTab == "corrections";
    public bool HasPending => PendingCount > 0;
    public string PendingText => PendingCount == 1 ? "1 record to review" : $"{PendingCount} records to review";
    public string ReviewTabText => PendingCount > 0 ? $"Review ({PendingCount})" : "Review";
    public string ReadOnlyText => IsReadOnly ? "This period is approved or paid out, so its records can't change here." : "";
    public bool IsConfirming => Confirming is not null;
    public string EmptyText => ActiveTab switch
    {
        "review" => "Nothing to review in this period.",
        "records" => "No records match.",
        _ => "No hour corrections in this period."
    };

    public override Task OnAppearingAsync() => LoadAsync();

    partial void OnStatusFilterChanged(string value) => Show();
    // the menu's Pending tag counts this pay period only, so looking at an older one doesn't change it
    partial void OnPendingCountChanged(int value)
    {
        if (PeriodStartsOn == PayrollViewModel.PeriodStartFor(PayrollViewModel.ManilaToday())) WeakReferenceMessenger.Default.Send(new AttendancePendingChanged(value));
    }

    [RelayCommand] public void OpenTab(string tab) { ActiveTab = tab is "records" or "corrections" ? tab : "review"; Confirming = null; Show(); }
    [RelayCommand] private Task PrevPeriodAsync() { PeriodStartsOn = PayrollViewModel.StepPrev(PeriodStartsOn); return LoadAsync(); }
    [RelayCommand] private Task NextPeriodAsync() { PeriodStartsOn = PayrollViewModel.StepNext(PeriodStartsOn); return LoadAsync(); }

    [RelayCommand]
    private async Task LoadAsync()
    {
        Confirming = null;
        _records = [];   // a failed read must not leave the last period's cards under this period's name
        Corrections.Clear();
        PayrollPeriod? period = null;
        if (!await RunAsync(() => svc.PayrollPeriodAsync(PeriodStartsOn), p => period = p) || period is null) return;
        PeriodId = period.PeriodId;
        IsReadOnly = period.IsPaid;
        // corrections are for people paid by the hour or the session (a fixed salary doesn't count hours)
        var counted = period.Rows.Where(r => r.PayType is "hourly" or "per_session").ToList();
        var keep = CorrectionPerson?.UserId;
        Fill(CorrectionPeople, IsReadOnly ? [] : counted);
        CorrectionPerson = CorrectionPeople.FirstOrDefault(p => p.UserId == keep);
        OnPropertyChanged(nameof(CanCorrect));
        await RunAsync(() => svc.PayrollRecordsAllAsync(PeriodId), all =>
        {
            _records = all.Records;
            Fill(Corrections, all.Adjustments.Select(a => new AttendanceCorrectionItem(a,
                counted.FirstOrDefault(r => r.UserId == a.UserId)?.PayType == "per_session" ? "sessions" : "h", !IsReadOnly)));
        });
        Show();
    }

    // ---- hour corrections (owner, 2026-11-25, option A: moved here from Payroll's pay details) ----
    public ObservableCollection<PayrollRow> CorrectionPeople { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CorrectionUnitLabel))] private PayrollRow? correctionPerson;
    [ObservableProperty] private string correctionHours = "";
    [ObservableProperty] private string correctionNote = "";
    [ObservableProperty] private string? correctionError;
    public bool CanCorrect => !IsReadOnly && CorrectionPeople.Count > 0;
    public string CorrectionUnitLabel => CorrectionPerson?.PayType == "per_session" ? "Sessions (+ or −)" : "Hours (+ or −)";

    [RelayCommand]
    private async Task AddCorrectionAsync()
    {
        CorrectionError = null;
        if (CorrectionPerson is not { } who) { CorrectionError = "Choose who the correction is for."; return; }
        if (!decimal.TryParse(CorrectionHours.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var q) || Math.Round(q, 2) == 0 || Math.Abs(q) > 1000)
        {
            CorrectionError = who.PayType == "per_session" ? "Enter sessions to add or take away, like 1 or -1." : "Enter hours to add or take away, like 1.5 or -2.";
            return;
        }
        var note = CorrectionNote.Trim();
        if (note.Length is 0 or > 200) { CorrectionError = "Add a short note (1–200 characters)."; return; }
        var res = await svc.AddPayrollHoursAsync(PeriodId, who.UserId, q, note);
        if (!res.Ok) { CorrectionError = ErrorText.For(res.Error, res.Message); return; }
        CorrectionHours = ""; CorrectionNote = "";
        Notice = $"Correction added for {who.FullName}.";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RemoveCorrectionAsync(AttendanceCorrectionItem? item)
    {
        if (item is null || !item.CanRemove) return;
        CorrectionError = null;
        var res = await svc.RemovePayrollHoursAsync(item.Adj.AdjustmentId);
        if (!res.Ok) { CorrectionError = ErrorText.For(res.Error, res.Message); return; }
        await LoadAsync();
    }

    void Show()
    {
        var cards = _records.Select(r => new AttendanceCardItem(r, !IsReadOnly, this)).ToList();
        PendingCount = IsReadOnly ? 0 : cards.Count(c => c.IsPending);
        Fill(Cards, ActiveTab == "review" ? cards.Where(c => c.IsPending)
            : StatusFilter == "All" ? cards : cards.Where(c => c.StatusLabel == StatusFilter));
    }

    internal void BeginApprove(AttendanceCardItem card)
    {
        ConfirmError = null;
        Fill(MoneyRows, card.MoneyRows());
        Confirming = card;
    }

    [RelayCommand] private void CancelApprove() { Confirming = null; ConfirmError = null; }

    [RelayCommand]
    private async Task ConfirmApproveAsync()
    {
        if (Confirming is not { } card) return;
        ConfirmError = await ReviewAsync(card, "approved", null);
        if (ConfirmError is null) Confirming = null;
    }

    internal async Task<string?> ReviewAsync(AttendanceCardItem card, string status, string? reason)
    {
        var res = await svc.ReviewPayrollTimeAsync(PeriodId, card.Rec.Source, card.Rec.SourceId, status, reason);
        if (!res.Ok) return ErrorText.For(res.Error, res.Message);
        await LoadAsync();
        return null;
    }
}
