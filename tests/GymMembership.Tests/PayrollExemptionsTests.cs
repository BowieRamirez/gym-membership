using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Plan 2026-11-24-payroll-exemptions-reopen.md (U1-U12), through AdminService and the view model on the demo gateway (mirror of 0145).
public class PayrollExemptionsTests
{
    static readonly DateOnly Oct1 = new(2026, 10, 1), Oct16 = new(2026, 10, 16);

    static async Task<(AdminService Admin, StaffSalary Desk)> Setup()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var desk = (await admin.PayrollStaffAsync()).Value!.First(s => s.Role == "employee");
        await admin.SetStaffSalaryAsync(desk.UserId, 13000m, "fixed");
        return (admin, desk);
    }

    [Fact]
    public async Task Minimum_wage_earner_pays_contributions_but_no_tax()
    {
        var (admin, desk) = await Setup();
        Assert.True((await admin.SetPayrollDeductionsAsync(desk.UserId, true, false, "Minimum wage earner")).Ok);
        var r = (await admin.PayrollPeriodAsync(Oct16)).Value!.Rows.First(x => x.UserId == desk.UserId);
        Assert.Equal((1300m, 0m, true, false, "Minimum wage earner"), (r.Sss, r.Tax, r.ContributionsOn, r.TaxOn, r.DeductionsReason));
        var s = (await admin.PayrollStaffAsync()).Value!.First(x => x.UserId == desk.UserId);
        Assert.Equal((true, false), (s.ContributionsOn, s.TaxOn));
    }

    [Fact]
    public async Task Thirteenth_month_is_tax_free_up_to_90000_a_year_and_de_minimis_never_taxed()
    {
        var (admin, desk) = await Setup();
        var jun = (await admin.PayrollPeriodAsync(new DateOnly(2025, 6, 1))).Value!.PeriodId;
        await admin.AddPayrollLineAsync(jun, desk.UserId, "bonus", 60000m, "13th month advance", "thirteenth_month");
        Assert.True((await admin.MarkPayrollPaidAsync(jun)).Ok);
        var dec = (await admin.PayrollPeriodAsync(new DateOnly(2025, 12, 1))).Value!.PeriodId;
        await admin.AddPayrollLineAsync(dec, desk.UserId, "bonus", 40000m, "13th month balance", "thirteenth_month");
        await admin.AddPayrollLineAsync(dec, desk.UserId, "bonus", 2000m, "Rice subsidy", "de_minimis");
        var r = (await admin.PayrollPeriodAsync(new DateOnly(2025, 12, 1))).Value!.Rows.First(x => x.UserId == desk.UserId);
        Assert.Equal((32000m, 100000m, 23000m), (r.Nontaxable, r.ThirteenthUsed, r.Taxable));   // 55,000 gross − 30,000 − 2,000
        Assert.Equal("bad_input", (await admin.AddPayrollLineAsync(dec, desk.UserId, "deduction", 5m, "x", "de_minimis")).Message);
        Assert.True((await admin.AddPayrollLineAsync(dec, desk.UserId, "deduction", 5m, "Overpaid", "retro")).Ok);
    }

    [Fact]
    public async Task Approve_reopen_and_paid_out()
    {
        var (admin, desk) = await Setup();
        var sep = (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 1))).Value!.PeriodId;
        Assert.Equal("not_approved", (await admin.MarkPayrollPaidOutAsync(sep)).Message);
        await admin.MarkPayrollPaidAsync(sep);
        Assert.Equal("approved", (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 1))).Value!.Status);
        Assert.Equal("bad_input", (await admin.ReopenPayrollAsync(sep, " ")).Message);
        var reopened = await admin.ReopenPayrollAsync(sep, "Wrong salary");
        Assert.True(reopened.Ok);
        Assert.Equal("draft", reopened.Value!.Status);
        Assert.DoesNotContain((await admin.PayrollLinesAsync(sep, desk.UserId)).Value!, l => l.Kind == "salary");
        await admin.MarkPayrollPaidAsync(sep);
        var s16 = (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 16))).Value!.PeriodId;
        await admin.MarkPayrollPaidAsync(s16);
        Assert.Equal("second_half_approved", (await admin.ReopenPayrollAsync(sep, "x")).Message);
        var paidOut = await admin.MarkPayrollPaidOutAsync(s16);
        Assert.Equal("paid_out", paidOut.Value!.Status);
        Assert.Equal("paid_out", (await admin.ReopenPayrollAsync(s16, "late")).Message);
        Assert.Contains("can't be reopened", ErrorText.For(AppErrorKind.Validation, "paid_out"));
    }

    [Fact]
    public void Payslip_says_draft_approved_or_paid_out()
    {
        var at = new DateTime(2026, 11, 25, 2, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Draft (not yet approved)", PayrollPdf.StatusLine(new PayrollPeriod()));
        Assert.Equal("Approved 25 Nov 2026 by Ana, not yet paid out", PayrollPdf.StatusLine(new PayrollPeriod { Status = "approved", PaidAt = at, PaidByName = "Ana" }));
        Assert.Equal("Paid out 25 Nov 2026 by Bo", PayrollPdf.StatusLine(new PayrollPeriod { Status = "paid_out", PaidAt = at, PaidOutAt = at, PaidOutByName = "Bo" }));
    }

    [Fact]
    public async Task Page_shows_status_buttons_two_switches_and_category_hints()
    {
        var (admin, desk) = await Setup();
        var vm = new PayrollViewModel(admin) { PeriodStartsOn = new DateOnly(2026, 9, 1) };
        await vm.OnAppearingAsync();
        Assert.Equal(("Draft", true, false, false), (vm.StatusText, vm.CanApprove, vm.CanReopen, vm.CanMarkPaidOut));
        await admin.MarkPayrollPaidAsync(vm.PeriodId);
        await vm.OnAppearingAsync();
        Assert.StartsWith("Approved", vm.StatusText);
        Assert.Equal((false, true, true), (vm.CanApprove, vm.CanReopen, vm.CanMarkPaidOut));
        Assert.Contains("after the money is sent", vm.PaidOutConfirmMessage());
        vm.ReopenReason = "";
        await vm.ReopenCommand.ExecuteAsync(null);
        Assert.Equal("Add a reason (1–200 characters).", vm.ReopenError);
        vm.ReopenReason = "Wrong salary";
        await vm.ReopenCommand.ExecuteAsync(null);
        Assert.Equal("Draft", vm.StatusText);

        Assert.Contains("13th month / other benefits", vm.LineCategories);
        Assert.Contains("De minimis", vm.LineCategories);
        Assert.Contains("Retro pay / adjustment", vm.LineCategories);
        await vm.OpenDrawerCommand.ExecuteAsync(vm.Rows.First(r => r.UserId == desk.UserId));
        vm.LineCategory = "13th month / other benefits";
        Assert.Equal("Tax-free up to ₱90,000.00 a year; ₱0.00 used this year.", vm.LineCategoryHint);
        vm.LineCategory = "De minimis";
        Assert.Contains("rice ₱2,500 a month", vm.LineCategoryHint);

        var item = vm.StaffSalaries.First(s => s.UserId == desk.UserId);
        item.BeginDeductionsCommand.Execute(null);
        item.TaxInput = false;
        item.DeductionsReasonInput = "Minimum wage earner";
        await item.SaveDeductionsCommand.ExecuteAsync(null);
        item = vm.StaffSalaries.First(s => s.UserId == desk.UserId);
        Assert.Equal("Contributions on · Tax off (Minimum wage earner)", item.DeductionsText);
    }
}
