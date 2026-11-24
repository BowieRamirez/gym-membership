using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Plan 2026-11-24-hourly-payroll.md Phase 2: the Payroll page's view model for hourly / per-session pay.
public class PayrollHourlyViewModelTests
{
    static readonly DateOnly Aug = new(2026, 8, 1);
    static DateTime Manila(int day, int hour) => new DateTime(2026, 8, day, hour, 0, 0, DateTimeKind.Utc).AddHours(-8);

    static async Task<(DemoGateway Gw, AdminService Admin, PayrollViewModel Vm, StaffSalary Desk)> Setup()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var desk = (await admin.PayrollStaffAsync()).Value!.First(s => s.Role == "employee");
        await admin.SetStaffSalaryAsync(desk.UserId, 80m, "hourly");
        var vm = new PayrollViewModel(admin) { PeriodStartsOn = Aug };
        return (gw, admin, vm, desk);
    }

    [Fact]
    public async Task Pending_shifts_block_mark_paid_until_each_is_approved_or_rejected_with_a_reason()
    {
        var (gw, _, vm, desk) = await Setup();
        gw.OwnerAddShift(desk.UserId, Manila(3, 8), Manila(3, 12));
        gw.OwnerAddShift(desk.UserId, Manila(4, 8), Manila(4, 10));
        await vm.OnAppearingAsync();

        var row = vm.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal("2 to approve", row.PendingText);
        Assert.Equal("0 h × ₱80.00", row.BasisText);
        Assert.False(vm.CanMarkPaid);
        Assert.Equal("Approve or reject 2 shifts or sessions first", vm.MarkPaidBlockedText);

        await vm.OpenDrawerCommand.ExecuteAsync(row);
        Assert.Equal(2, vm.DrawerRecords.Count);
        await vm.DrawerRecords[0].ApproveCommand.ExecuteAsync(null);

        var second = vm.DrawerRecords[1];
        second.BeginRejectCommand.Execute(null);
        await second.RejectCommand.ExecuteAsync(null);
        Assert.Equal("Add a reason (1–200 characters).", second.ErrorText);
        second.RejectReason = "Opened by mistake";
        await second.RejectCommand.ExecuteAsync(null);

        row = vm.Rows.First(r => r.UserId == desk.UserId);
        Assert.False(row.HasPending);
        Assert.Equal("4 h × ₱80.00", row.BasisText);
        Assert.Equal("₱320.00", row.BasePayText);
        Assert.Equal(new[] { "Approved", "Rejected" }, vm.DrawerRecords.Select(r => r.StatusLabel));
        Assert.Equal("Opened by mistake", vm.DrawerRecords[1].Reason);
        Assert.True(vm.CanMarkPaid);
    }

    [Fact]
    public async Task Approve_all_unflagged_leaves_long_shifts_pending_and_the_confirm_names_them()
    {
        var (gw, _, vm, desk) = await Setup();
        gw.OwnerAddShift(desk.UserId, Manila(3, 8), Manila(3, 12));
        gw.OwnerAddShift(desk.UserId, Manila(5, 6), Manila(5, 20));   // 14 h
        await vm.OnAppearingAsync();
        await vm.ApproveAllUnflaggedCommand.ExecuteAsync(null);
        var row = vm.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal("1 to approve", row.PendingText);
        Assert.True(row.IsFlagged);

        await vm.OpenDrawerCommand.ExecuteAsync(row);
        var longOne = vm.DrawerRecords.Single(r => r.Status == "pending");
        Assert.Contains("Over 12 hours", longOne.FlagText);
        await longOne.ApproveCommand.ExecuteAsync(null);
        Assert.Contains($"{desk.FullName} has records with warnings", vm.MarkPaidConfirmMessage());
    }

    [Fact]
    public async Task Hours_corrections_need_a_note_and_change_the_basis()
    {
        var (gw, _, vm, desk) = await Setup();
        gw.OwnerAddShift(desk.UserId, Manila(3, 8), Manila(3, 12));
        await vm.OnAppearingAsync();
        await vm.ApproveAllUnflaggedCommand.ExecuteAsync(null);
        await vm.OpenDrawerCommand.ExecuteAsync(vm.Rows.First(r => r.UserId == desk.UserId));
        Assert.True(vm.CanCorrectHours);

        vm.HoursInput = "1.5";
        await vm.AddHoursCommand.ExecuteAsync(null);
        Assert.Equal("Add a short note (1–200 characters).", vm.HoursError);
        vm.HoursNote = "Forgot to open the drawer";
        await vm.AddHoursCommand.ExecuteAsync(null);
        Assert.Null(vm.HoursError);
        Assert.Single(vm.DrawerAdjustments);
        Assert.Equal("+1.5 h", vm.DrawerAdjustments[0].QuantityText);
        Assert.Equal("Approved 4 h · corrections +1.5 h · payable 5.5 h × ₱80.00 = ₱440.00", vm.DrawerBasisText);
        Assert.Equal("5.5 h × ₱80.00 (+1.5 h)", vm.Rows.First(r => r.UserId == desk.UserId).BasisText);

        vm.HoursInput = "-1";
        vm.HoursNote = "Left early";
        await vm.AddHoursCommand.ExecuteAsync(null);
        await vm.RemoveHoursCommand.ExecuteAsync(vm.DrawerAdjustments.Last());
        Assert.Single(vm.DrawerAdjustments);
    }

    [Fact]
    public async Task Bonus_lines_carry_a_category_and_gross_is_base_plus_bonuses()
    {
        var (gw, _, vm, desk) = await Setup();
        gw.OwnerAddShift(desk.UserId, Manila(3, 8), Manila(3, 18));   // 10 h
        await vm.OnAppearingAsync();
        await vm.ApproveAllUnflaggedCommand.ExecuteAsync(null);
        await vm.OpenDrawerCommand.ExecuteAsync(vm.Rows.First(r => r.UserId == desk.UserId));

        Assert.Equal(new[] { "Regular bonus", "Overtime adjustment", "Holiday pay adjustment", "Other adjustment" }, vm.LineCategories);
        vm.LineCategory = "Overtime adjustment";
        vm.LineAmountText = "200";
        vm.LineNote = "Sunday cover";
        await vm.AddLineCommand.ExecuteAsync(null);
        vm.SetLineKindCommand.Execute("deduction");
        vm.LineAmountText = "50";
        vm.LineNote = "Late";
        await vm.AddLineCommand.ExecuteAsync(null);

        Assert.Equal("Overtime adjustment", vm.DrawerLines.First(l => l.Line.Kind == "bonus").CategoryLabel);
        Assert.Equal("Other adjustment", vm.DrawerLines.First(l => l.Line.Kind == "deduction").CategoryLabel);
        var row = vm.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal(("₱800.00", "₱1,000.00", "₱950.00"), (row.BasePayText, row.GrossText, row.NetText));
        Assert.Contains("₱1,000.00", vm.TotalGrossText);
    }

    [Fact]
    public async Task Period_heading_updates_when_stepping_from_the_16th_back_to_the_1st()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new PayrollViewModel(new AdminService(gw)) { PeriodStartsOn = new DateOnly(2026, 10, 16) };
        await vm.OnAppearingAsync();
        string? shown = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PayrollViewModel.PeriodLabel)) shown = vm.PeriodLabel; };
        await vm.PrevPeriodCommand.ExecuteAsync(null);
        Assert.Equal("1–15 Oct 2026", shown);   // the screen showed "1–31 Oct 2026": the end date changed without telling the heading
    }

    [Fact]
    public void Payslip_shows_the_basis_corrections_categories_gross_and_net()
    {
        var row = new PayrollRow
        {
            FullName = "Ana Cruz", Role = "employee", PayType = "hourly", Rate = 80m, Logged = 40m, Adjustment = 1.5m, Payable = 41.5m,
            BasePay = 3320m, Bonuses = 500m, Gross = 3820m, Deductions = 200m, Net = 3620m
        };
        var lines = new List<PayrollLine>
        {
            new() { Kind = "bonus", Amount = 500m, Note = "Sunday cover", Category = "overtime" },
            new() { Kind = "deduction", Amount = 200m, Note = "Late", Category = "other" }
        };
        var adjs = new List<PayrollHourAdjustment> { new() { Quantity = 1.5m, Note = "Forgot to open the drawer" } };
        var slip = PayrollPdf.Payslip(row, lines, adjs);
        Assert.Contains("Ana Cruz (Front desk) · Hourly pay, ₱80.00 per hour", slip);
        Assert.Contains("Approved 40 h · corrections +1.5 h · payable 41.5 h × ₱80.00 = ₱3,320.00", slip);
        Assert.Contains("Correction +1.5 h: Forgot to open the drawer", slip);
        Assert.Contains("+₱500.00 Overtime adjustment: Sunday cover", slip);
        Assert.Contains("Gross pay ₱3,820.00", slip);
        Assert.Contains("-₱200.00 Other adjustment: Late", slip);
        Assert.EndsWith("Net pay ₱3,620.00", slip);
    }

    [Fact]
    public async Task Salaries_tab_sets_pay_type_shows_personal_and_goes_back_to_the_role_default()
    {
        var (_, admin, vm, desk) = await Setup();
        await vm.OnAppearingAsync();
        vm.EmployeeRolePayType = "Hourly";
        vm.EmployeeRoleSalaryInput = "75";
        await vm.SaveEmployeeRoleSalaryCommand.ExecuteAsync(null);
        Assert.Equal("Current: ₱75.00 per hour", vm.EmployeeRoleSalaryStatus);

        var item = vm.StaffSalaries.First(s => s.UserId == desk.UserId);
        Assert.Equal("₱80.00 per hour", item.AmountText);
        Assert.Equal("Personal", item.SourceText);
        Assert.DoesNotContain("Per session", item.PayTypeOptions);
        await item.UseRoleDefaultCommand.ExecuteAsync(null);

        item = vm.StaffSalaries.First(s => s.UserId == desk.UserId);
        Assert.Equal(("₱75.00 per hour", "Role default"), (item.AmountText, item.SourceText));
        Assert.False(item.CanUseRoleDefault);

        var coach = vm.StaffSalaries.First(s => s.Data.Role == "coach");
        Assert.Contains("Per session", coach.PayTypeOptions);
        coach.BeginEditCommand.Execute(null);
        coach.PayTypeInput = "Per session";
        coach.AmountInput = "150";
        await coach.SaveCommand.ExecuteAsync(null);
        Assert.Equal("per_session", (await admin.PayrollStaffAsync()).Value!.First(s => s.UserId == coach.UserId).PayType);
    }
}
