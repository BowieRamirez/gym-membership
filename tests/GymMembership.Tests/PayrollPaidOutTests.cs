using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class PayrollPaidOutTests
{
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
    public async Task Front_desk_coach_and_member_cannot_list_paid_out_periods()
    {
        foreach (var email in new[] { "employee@demo.io", "coach@demo.io", "member@demo.io" })
        {
            var gw = new DemoGateway();
            await gw.SignInAsync(email, DemoGateway.DemoPassword);
            var svc = new AdminService(gw);
            Assert.Equal(AppErrorKind.Unauthorized, (await svc.PayrollPaidOutPeriodsAsync()).Error);
        }
    }

    [Fact]
    public async Task Paid_out_periods_list_only_paid_out_periods_newest_first_with_exact_totals()
    {
        var (admin, desk) = await Setup();
        await admin.PayrollPeriodAsync(new DateOnly(2026, 7, 1));   // draft
        var aug = (await admin.PayrollPeriodAsync(new DateOnly(2026, 8, 1))).Value!;
        await admin.MarkPayrollPaidAsync(aug.PeriodId);             // approved, not paid out

        var sep1 = (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 1))).Value!;
        await admin.AddPayrollLineAsync(sep1.PeriodId, desk.UserId, "bonus", 1000m, "Cover", "regular_bonus");
        await admin.MarkPayrollPaidAsync(sep1.PeriodId);
        await admin.MarkPayrollPaidOutAsync(sep1.PeriodId);
        var sep1Detail = (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 1))).Value!;

        var sep16 = (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 16))).Value!;
        await admin.MarkPayrollPaidAsync(sep16.PeriodId);
        await admin.MarkPayrollPaidOutAsync(sep16.PeriodId);
        var sep16Detail = (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 16))).Value!;

        var list = (await admin.PayrollPaidOutPeriodsAsync()).Value!;
        Assert.Equal(2, list.Count);
        Assert.Equal(new DateOnly(2026, 9, 16), list[0].StartsOn);
        Assert.Equal(new DateOnly(2026, 9, 1), list[1].StartsOn);

        Assert.Equal((sep16Detail.TotalGross, sep16Detail.TotalGov, sep16Detail.TotalNet), (list[0].Gross, list[0].GovTotal, list[0].Net));
        Assert.Equal((1000m, sep1Detail.TotalGross, sep1Detail.TotalNet), (list[1].Bonuses, list[1].Gross, list[1].Net));
        Assert.DoesNotContain(list, p => p.PeriodId == aug.PeriodId);
    }

    [Fact]
    public async Task ViewModel_paid_out_tab_loads_updates_on_payout_and_opens_selected_period()
    {
        var (admin, desk) = await Setup();
        var sep1 = (await admin.PayrollPeriodAsync(new DateOnly(2026, 9, 1))).Value!;
        await admin.MarkPayrollPaidAsync(sep1.PeriodId);

        var vm = new PayrollViewModel(admin) { PeriodStartsOn = new DateOnly(2026, 9, 1) };
        await vm.OnAppearingAsync();
        Assert.Empty(vm.PaidOutPeriods);
        Assert.Equal("No paid-out periods yet", vm.PaidOutSummaryText);

        // Marking as paid out updates the Paid out table immediately
        var oldAnswer = Confirm.Answer;
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        try
        {
            await vm.MarkPaidOutCommand.ExecuteAsync(null);
        }
        finally { Confirm.Answer = oldAnswer; }
        Assert.Single(vm.PaidOutPeriods);
        Assert.Equal("1 paid-out period · newest first", vm.PaidOutSummaryText);

        await vm.OpenPaidOutTabCommand.ExecuteAsync(null);
        Assert.True(vm.IsPaidOutTab);
        Assert.False(vm.IsPeriodTab);
        Assert.False(vm.IsSalariesTab);

        // Switch away to a later draft period, then open the paid-out period from the Paid out table
        await vm.NextPeriodCommand.ExecuteAsync(null);
        Assert.Equal(new DateOnly(2026, 9, 16), vm.PeriodStartsOn);

        await vm.OpenPaidOutTabCommand.ExecuteAsync(null);
        await vm.OpenPaidOutPeriodCommand.ExecuteAsync(vm.PaidOutPeriods[0]);
        Assert.True(vm.IsPeriodTab);
        Assert.False(vm.IsPaidOutTab);
        Assert.Equal(new DateOnly(2026, 9, 1), vm.PeriodStartsOn);
        Assert.Contains(vm.Rows, r => r.UserId == desk.UserId);
    }

    [Fact]
    public void PayrollPage_has_responsive_paid_out_table_for_fullscreen_and_minimized()
    {
        var xaml = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Views", "PayrollPage.xaml"));
        Assert.Contains("AutomationId=\"PayrollPaidOutTab\"", xaml);
        Assert.Contains("x:Name=\"PaidOutHead\"", xaml);
        Assert.Contains("RowTemplate=\"{StaticResource PaidOutRowTemplate}\"", xaml);
        Assert.Contains("CardTemplate=\"{StaticResource PaidOutCardTemplate}\"", xaml);
        Assert.Contains("Header=\"{x:Reference PaidOutHead}\"", xaml);
        Assert.Contains("Items=\"{Binding PaidOutPeriods}\"", xaml);
    }
}
