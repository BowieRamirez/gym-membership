using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

// Owner fixes 2026-11-25, Fix 5 (X1-X6): what the gym spends (Expenses) and how it is doing (Financial reports). System admin and Admin.

/// <summary>One expense row; a mistake is voided with a reason (X3).</summary>
public sealed partial class ExpenseItem(Expense e, ExpensesViewModel page) : ObservableObject
{
    public Expense E { get; } = e;
    public string DayText => E.SpentOn.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
    public string CategoryText => ExpenseCategories.Name(E.Category);
    public string AmountText => PayTypes.Money(E.Amount);
    public string Note => E.Note;
    public string ByText => $"Added by {E.CreatedByName}";
    public bool IsVoided => E.IsVoided;
    public bool CanVoid => !E.IsVoided;
    public string VoidText => E.IsVoided ? $"Voided by {E.VoidedByName}: {E.VoidReason}" : "";

    [ObservableProperty] private bool isVoiding;
    [ObservableProperty] private string voidReason = "";
    [ObservableProperty] private string? errorText;

    [RelayCommand] private void BeginVoid() { IsVoiding = true; ErrorText = null; }
    [RelayCommand] private void CancelVoid() { IsVoiding = false; ErrorText = null; }
    [RelayCommand]
    private async Task VoidAsync()
    {
        var reason = VoidReason.Trim();
        if (reason.Length is 0 or > 200) { ErrorText = "Say why it's voided (1–200 characters)."; return; }
        ErrorText = await page.VoidAsync(this, reason);
    }
}

public partial class ExpensesViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<ExpenseItem> Items { get; } = new();
    public IReadOnlyList<string> CategoryOptions { get; } = ExpenseCategories.All.Select(c => c.Name).ToList();

    static DateTime Today => PayrollViewModel.ManilaToday().ToDateTime(TimeOnly.MinValue);
    [ObservableProperty] private DateTime from = new(Today.Year, Today.Month, 1);
    [ObservableProperty] private DateTime to = Today;
    [ObservableProperty] private string totalText = "₱0.00";
    [ObservableProperty] private string summaryText = "";

    // the add form
    [ObservableProperty] private DateTime spentOn = Today;
    [ObservableProperty] private string category = "Electricity";
    [ObservableProperty] private string amountText = "";
    [ObservableProperty] private string note = "";
    [ObservableProperty] private string? addError;
    public DateTime MaxDay => Today;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (To < From) { ErrorMessage = "The end date is before the start date."; return; }
        await RunAsync(() => svc.ExpensesAsync(DateOnly.FromDateTime(From), DateOnly.FromDateTime(To)), list =>
        {
            Fill(Items, list.Select(e => new ExpenseItem(e, this)));
            var counted = list.Where(e => !e.IsVoided).ToList();
            TotalText = PayTypes.Money(counted.Sum(e => e.Amount));
            SummaryText = counted.Count == 1 ? "1 expense" : $"{counted.Count} expenses";
        });
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        AddError = null; Notice = null;
        if (ExpenseCategories.Code(Category) is not { } code) { AddError = "Choose a category."; return; }
        if (!decimal.TryParse(AmountText.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0 || amount > 10_000_000 || Math.Round(amount, 2) != amount)
        { AddError = "Enter an amount above ₱0, like 1500 or 1500.50."; return; }
        var what = Note.Trim();
        if (what.Length is 0 or > 200) { AddError = "Say what it was for (1–200 characters)."; return; }
        if (SpentOn.Date > Today) { AddError = "The day can't be in the future."; return; }
        var res = await svc.AddExpenseAsync(DateOnly.FromDateTime(SpentOn), code, amount, what);
        if (!res.Ok) { AddError = ErrorText.For(res.Error, res.Message); return; }
        AmountText = ""; Note = "";
        Notice = $"{Category} {PayTypes.Money(amount)} added.";
        await LoadAsync();
    }

    internal async Task<string?> VoidAsync(ExpenseItem item, string reason)
    {
        var res = await svc.VoidExpenseAsync(item.E.ExpenseId, reason);
        if (!res.Ok) return ErrorText.For(res.Error, res.Message);
        Notice = $"{item.CategoryText} {item.AmountText} voided.";
        await LoadAsync();
        return null;
    }
}

/// <summary>X5: a month or a whole year; for a year, the months one under the other.</summary>
public partial class FinanceViewModel(IAdminService svc) : LoadableViewModel
{
    public ObservableCollection<FinanceLine> ByMethod { get; } = new();
    public ObservableCollection<FinanceLine> ByCategory { get; } = new();
    public ObservableCollection<FinanceMonth> Months { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SpanText))] private int year = PayrollViewModel.ManilaToday().Year;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SpanText))] private int month = PayrollViewModel.ManilaToday().Month;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SpanText), nameof(IsMonth))] private bool isYear;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RevenueText), nameof(ExpensesText), nameof(PayrollText), nameof(NetText), nameof(IsLoss))]
    private FinanceTotals? totals;

    public bool IsMonth => !IsYear;
    public string SpanText => IsYear ? Year.ToString(CultureInfo.InvariantCulture) : new DateTime(Year, Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    public string RevenueText => PayTypes.Money(Totals?.Revenue ?? 0);
    public string ExpensesText => PayTypes.Money(Totals?.Expenses ?? 0);
    public string PayrollText => PayTypes.Money(Totals?.Payroll ?? 0);
    public string NetText => FinanceReport.Signed(Totals?.Net ?? 0);
    public bool IsLoss => Totals?.Net < 0;

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand] private Task ShowMonthAsync() { IsYear = false; return LoadAsync(); }
    [RelayCommand] private Task ShowYearAsync() { IsYear = true; return LoadAsync(); }

    [RelayCommand]
    private Task PrevAsync()
    {
        if (IsYear) Year--; else if (Month == 1) { Month = 12; Year--; } else Month--;
        return LoadAsync();
    }

    [RelayCommand]
    private Task NextAsync()
    {
        if (IsYear) Year++; else if (Month == 12) { Month = 1; Year++; } else Month++;
        return LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var from = IsYear ? new DateOnly(Year, 1, 1) : new DateOnly(Year, Month, 1);
        var to = IsYear ? new DateOnly(Year, 12, 31) : from.AddMonths(1).AddDays(-1);
        IReadOnlyList<RevenueRow> revenue = []; IReadOnlyList<Expense> expenses = []; IReadOnlyList<PayrollCostDay> payroll = [];
        if (!await RunAsync(() => svc.RevenueAsync(from, to), r => revenue = r)) return;
        if (!await RunAsync(() => svc.ExpensesAsync(from, to), e => expenses = e)) return;
        if (!await RunAsync(() => svc.PayrollCostAsync(from, to), p => payroll = p)) return;
        Totals = FinanceReport.Totals(revenue, expenses, payroll);
        Fill(ByMethod, Totals.ByMethod);
        Fill(ByCategory, Totals.ByCategory);
        Fill(Months, IsYear ? FinanceReport.Months(revenue, expenses, payroll) : []);
    }
}
