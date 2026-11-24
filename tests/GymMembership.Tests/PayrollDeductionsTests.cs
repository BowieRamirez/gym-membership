using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Plan 2026-11-24-payroll-deductions.md (T1-T12), through AdminService and the view model on the demo gateway (mirror of 0144).
public class PayrollDeductionsTests
{
    static readonly DateOnly Oct1 = new(2026, 10, 1), Oct16 = new(2026, 10, 16);

    static async Task<(AdminService Admin, StaffSalary Desk, StaffSalary Coach)> Setup()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var staff = (await admin.PayrollStaffAsync()).Value!;
        var desk = staff.First(s => s.Role == "employee");
        await admin.SetStaffSalaryAsync(desk.UserId, 13000m, "fixed");
        return (admin, desk, staff.First(s => s.Role == "coach"));
    }

    [Theory]   // Review Focus 1: bracket edges, same numbers as payroll-deductions.mjs
    [InlineData(10417, 0)] [InlineData(10417.01, 0)] [InlineData(16667, 937.5)] [InlineData(20000, 1604.1)]
    public void Withholding_tax_follows_the_semi_monthly_table(decimal taxable, decimal tax) => Assert.Equal(tax, PayrollDeductions.Tax(taxable));

    [Theory]
    [InlineData(5249.99, 250)] [InlineData(5250, 275)] [InlineData(34750, 1750)] [InlineData(90000, 1750)]
    public void Sss_uses_the_monthly_salary_credit(decimal pay, decimal share) => Assert.Equal(share, PayrollDeductions.Sss(pay));

    [Fact]
    public void PhilHealth_and_PagIbig_floors_and_caps()
    {
        Assert.Equal((250m, 650m, 2500m), (PayrollDeductions.PhilHealth(8000), PayrollDeductions.PhilHealth(26000), PayrollDeductions.PhilHealth(120000)));
        Assert.Equal((15m, 30m, 200m), (PayrollDeductions.PagIbig(1500), PayrollDeductions.PagIbig(1500.01m), PayrollDeductions.PagIbig(20000)));
    }

    [Fact]
    public async Task First_half_takes_tax_only_and_second_half_the_months_contributions()
    {
        var (admin, desk, _) = await Setup();
        var a = (await admin.PayrollPeriodAsync(Oct1)).Value!.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal((0m, 387.45m, 13000m - 387.45m), (a.Sss + a.PhilHealth + a.PagIbig, a.Tax, a.Net));
        var b = (await admin.PayrollPeriodAsync(Oct16)).Value!.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal((1300m, 650m, 200m, 64.95m), (b.Sss, b.PhilHealth, b.PagIbig, b.Tax));
        Assert.Equal((2214.95m, 2214.95m, 13000m - 2214.95m), (b.GovTotal, b.Deductions, b.Net));
    }

    [Fact]
    public async Task Second_half_waits_for_the_first_then_freezes_the_government_lines()
    {
        var (admin, desk, _) = await Setup();
        var p2 = (await admin.PayrollPeriodAsync(Oct16)).Value!.PeriodId;
        var refused = await admin.MarkPayrollPaidAsync(p2);
        Assert.Equal("first_half_unpaid", refused.Message);
        Assert.Contains("1st–15th", ErrorText.For(refused.Error, refused.Message));
        Assert.True((await admin.MarkPayrollPaidAsync((await admin.PayrollPeriodAsync(Oct1)).Value!.PeriodId)).Ok);
        Assert.True((await admin.MarkPayrollPaidAsync(p2)).Ok);
        var lines = (await admin.PayrollLinesAsync(p2, desk.UserId)).Value!;
        Assert.Equal(1300m, lines.Single(l => l.Category == "sss").Amount);
        Assert.Equal("SSS", lines.Single(l => l.Category == "sss").CategoryLabel);
        Assert.Equal(64.95m, lines.Single(l => l.Category == "tax").Amount);
        await admin.SetStaffSalaryAsync(desk.UserId, 50000m, "fixed");
        Assert.Equal(64.95m, (await admin.PayrollPeriodAsync(Oct16)).Value!.Rows.First(r => r.UserId == desk.UserId).Tax);
    }

    [Fact]
    public async Task Switched_off_people_have_no_government_deductions_and_low_pay_is_cut_at_zero()
    {
        var (admin, desk, coach) = await Setup();
        Assert.True((await admin.SetPayrollDeductionsAsync(desk.UserId, false)).Ok);
        Assert.False((await admin.PayrollStaffAsync()).Value!.First(s => s.UserId == desk.UserId).DeductionsOn);
        var r = (await admin.PayrollPeriodAsync(Oct16)).Value!.Rows.First(x => x.UserId == desk.UserId);
        Assert.Equal((0m, 13000m), (r.GovTotal, r.Net));
        await admin.SetStaffSalaryAsync(coach.UserId, 100m, "hourly");   // no hours: nothing to take from
        var c = (await admin.PayrollPeriodAsync(Oct16)).Value!.Rows.First(x => x.UserId == coach.UserId);
        Assert.Equal((0m, 0m, true), (c.GovTotal, c.Net, c.GovCut));
    }

    [Fact]
    public async Task Page_shows_government_lines_switch_and_confirm()
    {
        var (admin, desk, _) = await Setup();
        var vm = new PayrollViewModel(admin) { PeriodStartsOn = Oct16 };
        await vm.OnAppearingAsync();
        var row = vm.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal("₱2,214.95", row.DeductionsText);
        Assert.Equal("Gov't ₱2,214.95", row.GovText);
        Assert.Contains("Gov't deductions", vm.TotalGrossText);
        await vm.OpenDrawerCommand.ExecuteAsync(row);
        Assert.Equal(new[] { "SSS", "PhilHealth", "Pag-IBIG", "Withholding tax" }, vm.DrawerGov.Select(g => g.Label));
        Assert.Equal("MSC ₱26,000.00", vm.DrawerGov[0].BasisText);
        Assert.Equal("-₱1,300.00", vm.DrawerGov[0].AmountText);
        Assert.Contains("Government deductions", vm.MarkPaidConfirmMessage());

        vm.PeriodStartsOn = Oct1;
        await vm.OnAppearingAsync();
        await vm.OpenDrawerCommand.ExecuteAsync(vm.Rows.First(r => r.UserId == desk.UserId));
        Assert.Equal("Withholding tax", Assert.Single(vm.DrawerGov).Label);
        Assert.Equal("SSS, PhilHealth and Pag-IBIG are taken in the 16th–end pay.", vm.DrawerGovNote);

        var item = vm.StaffSalaries.First(s => s.UserId == desk.UserId);
        Assert.True(item.DeductionsOn);
        await item.ToggleDeductionsCommand.ExecuteAsync(null);
        Assert.False(vm.StaffSalaries.First(s => s.UserId == desk.UserId).DeductionsOn);
    }

    [Fact]
    public void Payslip_lists_government_lines_between_gross_and_net()
    {
        var row = new PayrollRow { FullName = "Ana", Role = "employee", PayType = "fixed", Rate = 13000, Payable = 1, BasePay = 13000, Gross = 13000,
                                   Deductions = 2214.95m, Net = 10785.05m };
        var lines = new List<PayrollLine>
        {
            new() { Kind = "deduction", Amount = 1300, Category = "sss", Note = "SSS, Oct 2026, on ₱26,000.00 month pay" },
            new() { Kind = "deduction", Amount = 64.95m, Category = "tax", Note = "Withholding tax, 16–31 Oct 2026, on ₱10,850.00 taxable" }
        };
        var slip = PayrollPdf.Payslip(row, lines, []);
        Assert.True(slip.IndexOf("Gross pay") < slip.IndexOf("-₱1,300.00 SSS") && slip.IndexOf("-₱1,300.00 SSS") < slip.IndexOf("Net pay"));
        Assert.Contains("-₱64.95 Withholding tax: Withholding tax, 16–31 Oct 2026", slip);
    }
}
