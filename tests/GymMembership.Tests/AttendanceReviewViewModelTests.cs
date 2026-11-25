using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner fixes 2026-11-25, Fix 4 Phase 4.2 (W2, W4, W6, W7): Attendance Review, Records and Corrections across every person.
public class AttendanceReviewViewModelTests
{
    static readonly DateOnly Aug = new(2026, 8, 1);
    static DateTime Manila(int day, int hour, int minute = 0) => new DateTime(2026, 8, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-8);

    static async Task<(DemoGateway Gw, AdminService Admin, AttendanceReviewViewModel Vm, StaffSalary Desk, StaffSalary Coach)> Open(Action<DemoGateway, StaffSalary, StaffSalary>? seed = null)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var staff = (await admin.PayrollStaffAsync()).Value!;
        var desk = staff.First(s => s.Role == "employee");
        var coach = staff.First(s => s.Role == "coach");
        Assert.True((await admin.SetStaffSalaryAsync(desk.UserId, 130m, "hourly")).Ok);
        seed?.Invoke(gw, desk, coach);
        var vm = new AttendanceReviewViewModel(admin) { PeriodStartsOn = Aug };
        await vm.LoadCommand.ExecuteAsync(null);
        return (gw, admin, vm, desk, coach);
    }

    static void TwoShifts(DemoGateway gw, StaffSalary desk, StaffSalary _)
    {
        gw.OwnerAddShift(desk.UserId, Manila(5, 8), Manila(5, 17));
        gw.OwnerAddShift(desk.UserId, Manila(6, 8), Manila(6, 12, 16));   // 4.27 h
    }

    [Fact]
    public async Task Review_lists_pending_records_of_every_person_with_name_and_role()
    {
        var (_, admin, vm, desk, coach) = await Open((gw, d, c) =>
        {
            TwoShifts(gw, d, c);
        });
        Assert.True((await admin.SetStaffSalaryAsync(coach.UserId, 150m, "per_session")).Ok);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.PendingCount);
        var card = vm.Cards.First(c => c.DateText == "August 5, 2026");
        Assert.Equal((desk.FullName, "Front Desk", "8:00 AM", "5:00 PM", "Pending"), (card.FullName, card.RoleText, card.InText, card.OutText, card.StatusLabel));
        Assert.Equal(2, card.Initials.Length);
        Assert.All(vm.Cards, c => Assert.Equal("Pending", c.StatusLabel));
    }

    [Fact]
    public async Task Approve_shows_the_money_first_then_confirm_approves()
    {
        var (_, _, vm, _, _) = await Open(TwoShifts);
        var card = vm.Cards.First(c => c.DateText == "August 6, 2026");
        card.ApproveCommand.Execute(null);
        Assert.Same(card, vm.Confirming);
        Assert.Equal(2, vm.PendingCount);   // nothing approved yet
        Assert.Equal(new[] { ("Recorded hours", "4.27 hours"), ("Approved hours", "0 hours"), ("Hourly rate", "₱130.00"), ("Potential payable amount", "₱555.10") },
            vm.MoneyRows.Select(r => (r.Label, r.Value)));

        await vm.ConfirmApproveCommand.ExecuteAsync(null);
        Assert.Null(vm.Confirming);
        Assert.Equal(1, vm.PendingCount);
        Assert.DoesNotContain(vm.Cards, c => c.DateText == "August 6, 2026");   // Review shows pending only
    }

    [Fact]   // review: two overlapping records each show their full amount, but the shared time is paid once
    public async Task Overlapping_record_says_the_shared_time_is_paid_once()
    {
        var (_, _, vm, _, _) = await Open((gw, d, _) => { gw.OwnerAddShift(d.UserId, Manila(7, 8), Manila(7, 12)); gw.OwnerAddShift(d.UserId, Manila(7, 11), Manila(7, 13)); });
        vm.Cards[0].ApproveCommand.Execute(null);
        Assert.Equal(("Overlaps another record", "The shared time is paid once, so the period total is lower"), (vm.MoneyRows[^1].Label, vm.MoneyRows[^1].Value));
    }

    [Fact]
    public async Task Cancel_leaves_the_record_pending()
    {
        var (_, _, vm, _, _) = await Open(TwoShifts);
        vm.Cards[0].ApproveCommand.Execute(null);
        vm.CancelApproveCommand.Execute(null);
        Assert.Null(vm.Confirming);
        Assert.Equal(2, vm.PendingCount);
    }

    [Fact]
    public async Task Per_session_amount_is_the_session_rate()
    {
        var (gw, admin, vm, _, coach) = await Open();
        Assert.True((await admin.SetStaffSalaryAsync(coach.UserId, 150m, "per_session")).Ok);
        gw.OwnerAddSession(new TrainingSession { CoachId = coach.UserId, MemberId = "u-alex", ScheduledStart = Manila(9, 9), ScheduledEnd = Manila(9, 10), Status = "completed" });
        await vm.LoadCommand.ExecuteAsync(null);
        var card = Assert.Single(vm.Cards);
        Assert.Equal("Coach", card.RoleText);
        card.ApproveCommand.Execute(null);
        Assert.Equal(new[] { ("Recorded", "1 session"), ("Approved", "0 sessions"), ("Rate", "₱150.00 per session"), ("Potential payable amount", "₱150.00") },
            vm.MoneyRows.Select(r => (r.Label, r.Value)));
    }

    [Fact]
    public async Task Reject_needs_a_reason_and_leaves_Review()
    {
        var (_, _, vm, _, _) = await Open(TwoShifts);
        var card = vm.Cards[0];
        card.BeginRejectCommand.Execute(null);
        await card.RejectCommand.ExecuteAsync(null);
        Assert.Equal("Add a reason (1–200 characters).", card.ErrorText);
        card.RejectReason = "Opened by mistake";
        await card.RejectCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.PendingCount);
        Assert.Single(vm.Cards);
    }

    [Fact]
    public async Task Records_filter_by_status()
    {
        var (_, _, vm, _, _) = await Open(TwoShifts);
        vm.Cards[0].ApproveCommand.Execute(null);
        await vm.ConfirmApproveCommand.ExecuteAsync(null);
        vm.OpenTabCommand.Execute("records");
        Assert.Equal(2, vm.Cards.Count);
        vm.StatusFilter = "Approved";
        Assert.Equal("Approved", Assert.Single(vm.Cards).StatusLabel);
        vm.StatusFilter = "Pending";
        Assert.Equal("Pending", Assert.Single(vm.Cards).StatusLabel);
        vm.StatusFilter = "Rejected";
        Assert.Empty(vm.Cards);
    }

    [Fact]
    public async Task Corrections_list_the_periods_hour_corrections()
    {
        var (_, admin, vm, desk, _) = await Open(TwoShifts);
        Assert.True((await admin.AddPayrollHoursAsync(vm.PeriodId, desk.UserId, -0.5m, "Long lunch")).Ok);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.OpenTabCommand.Execute("corrections");
        var c = Assert.Single(vm.Corrections);
        Assert.Equal((desk.FullName, "-0.5 h", "Long lunch"), (c.FullName, c.QuantityText, c.Note));
    }

    [Fact]
    public async Task Paid_out_period_is_read_only()
    {
        var (_, admin, vm, _, _) = await Open(TwoShifts);
        Assert.True((await admin.ApproveAllPayrollTimeAsync(vm.PeriodId, null)).Ok);
        Assert.True((await admin.MarkPayrollPaidAsync(vm.PeriodId)).Ok);
        await vm.LoadCommand.ExecuteAsync(null);
        vm.OpenTabCommand.Execute("records");
        Assert.True(vm.IsReadOnly);
        Assert.All(vm.Cards, c => Assert.False(c.CanApprove || c.CanReject));
    }
}
