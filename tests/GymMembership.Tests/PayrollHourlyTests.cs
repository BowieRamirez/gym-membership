using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

// Plan 2026-11-24-hourly-payroll.md (H1-H17), through AdminService on the demo gateway (mirror of 0142).
public class PayrollHourlyTests
{
    static readonly DateOnly Aug = new(2026, 8, 1);
    static DateTime Manila(int day, int hour, int minute = 0) => new DateTime(2026, 8, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-8);

    static async Task<(DemoGateway Gw, AdminService Admin, StaffSalary Desk, StaffSalary Coach)> Setup()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var staff = (await admin.PayrollStaffAsync()).Value!;
        return (gw, admin, staff.First(s => s.Role == "employee"), staff.First(s => s.Role == "coach"));
    }

    [Fact]
    public async Task Hourly_desk_is_paid_only_for_approved_shifts_and_pending_blocks_mark_paid()
    {
        var (gw, admin, desk, _) = await Setup();
        Assert.True((await admin.SetStaffSalaryAsync(desk.UserId, 80m, "hourly")).Ok);
        var s1 = gw.OwnerAddShift(desk.UserId, Manila(3, 8), Manila(3, 12));
        var s2 = gw.OwnerAddShift(desk.UserId, Manila(4, 8), Manila(4, 11, 30));
        var period = (await admin.PayrollPeriodAsync(Aug)).Value!;
        var row = period.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal(("hourly", 0m, 2, 0m), (row.PayType, row.Logged, row.Pending, row.BasePay));

        var recs = (await admin.PayrollRecordsAsync(period.PeriodId, desk.UserId)).Value!;
        Assert.Equal(2, recs.Count);
        Assert.All(recs, r => Assert.Equal("pending", r.Status));

        Assert.True((await admin.ReviewPayrollTimeAsync(period.PeriodId, "cash_shift", s1, "approved")).Ok);
        Assert.Equal("bad_input", (await admin.ReviewPayrollTimeAsync(period.PeriodId, "cash_shift", s2, "rejected", " ")).Message);
        var paid = await admin.MarkPayrollPaidAsync(period.PeriodId);
        Assert.StartsWith("time_pending:", paid.Message);
        Assert.Contains(desk.FullName, ErrorText.For(paid.Error, paid.Message));

        Assert.True((await admin.ReviewPayrollTimeAsync(period.PeriodId, "cash_shift", s2, "rejected", "Opened by mistake")).Ok);
        row = (await admin.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal((4m, 0, 320m), (row.Logged, row.Pending, row.BasePay));
        Assert.Equal("Opened by mistake", (await admin.PayrollRecordsAsync(period.PeriodId, desk.UserId)).Value!.First(r => r.SourceId == s2).Reason);
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Fact]
    public async Task Hourly_periods_are_paid_from_the_day_after_and_paid_periods_show_nothing_pending()
    {
        var gw = new DemoGateway(clock: new Clock(new DateTimeOffset(2026, 8, 15, 2, 0, 0, TimeSpan.Zero)));   // 10 am Manila, last day of 1-15 Aug
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var desk = (await admin.PayrollStaffAsync()).Value!.First(s => s.Role == "employee");
        await admin.SetStaffSalaryAsync(desk.UserId, 80m, "hourly");
        var pid = (await admin.PayrollPeriodAsync(Aug)).Value!.PeriodId;
        Assert.Equal("period_not_ended", (await admin.MarkPayrollPaidAsync(pid)).Message);

        var later = new DemoGateway(clock: new Clock(new DateTimeOffset(2026, 8, 16, 2, 0, 0, TimeSpan.Zero)));
        await later.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin2 = new AdminService(later);
        await admin2.SetStaffSalaryAsync(desk.UserId, 80m, "hourly");
        pid = (await admin2.PayrollPeriodAsync(Aug)).Value!.PeriodId;
        Assert.True((await admin2.MarkPayrollPaidAsync(pid)).Ok);
        later.OwnerAddShift(desk.UserId, Manila(15, 18), Manila(15, 21));   // closed after paying
        Assert.Equal(0, (await admin2.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == desk.UserId).Pending);
    }

    [Fact]
    public async Task Overlapping_approved_shifts_count_once_and_approve_all_skips_flagged()
    {
        var (gw, admin, desk, _) = await Setup();
        await admin.SetStaffSalaryAsync(desk.UserId, 100m, "hourly");
        gw.OwnerAddShift(desk.UserId, Manila(5, 8), Manila(5, 12));
        gw.OwnerAddShift(desk.UserId, Manila(5, 11), Manila(5, 14));
        gw.OwnerAddShift(desk.UserId, Manila(6, 8), Manila(6, 10));
        gw.OwnerAddShift(desk.UserId, Manila(7, 6), Manila(7, 19));   // 13 h → long
        var pid = (await admin.PayrollPeriodAsync(Aug)).Value!.PeriodId;

        Assert.Equal(1, (await admin.ApproveAllPayrollTimeAsync(pid, desk.UserId)).Value);
        var recs = (await admin.PayrollRecordsAsync(pid, desk.UserId)).Value!;
        Assert.Equal(2, recs.Count(r => r.Flags.Contains("overlap")));
        Assert.Contains(recs, r => r.Flags.Contains("long") && r.Status == "pending");
        foreach (var r in recs.Where(r => r.Flags.Contains("overlap")))
            Assert.True((await admin.ReviewPayrollTimeAsync(pid, r.Source, r.SourceId, "approved")).Ok);
        var row = (await admin.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal(8m, row.Logged);   // 6 h merged + 2 h
        Assert.Equal(1, row.Pending);
    }

    [Fact]
    public async Task Personal_rate_survives_a_role_default_and_use_role_default_goes_back()
    {
        var (_, admin, desk, _) = await Setup();
        Assert.True((await admin.SetRoleSalaryAsync("employee", 80m, "hourly")).Ok);
        Assert.True((await admin.SetStaffSalaryAsync(desk.UserId, 90m, "hourly")).Ok);
        Assert.True((await admin.SetRoleSalaryAsync("employee", 85m, "hourly")).Ok);
        var me = (await admin.PayrollStaffAsync()).Value!.First(s => s.UserId == desk.UserId);
        Assert.Equal((90m, true, "hourly"), (me.Amount, me.Personal, me.PayType));
        Assert.True((await admin.UseRoleSalaryAsync(desk.UserId)).Ok);
        me = (await admin.PayrollStaffAsync()).Value!.First(s => s.UserId == desk.UserId);
        Assert.Equal((85m, false), (me.Amount, me.Personal));
        Assert.Equal("per_session_coach_only", (await admin.SetStaffSalaryAsync(desk.UserId, 150m, "per_session")).Message);
        // already following the role: nothing changes and nothing is logged (0143)
        var logged = (await admin.AuditExportAsync(new AuditFilter(Area: "payroll"))).Value!.Count;
        Assert.True((await admin.UseRoleSalaryAsync(desk.UserId)).Ok);
        Assert.Equal(logged, (await admin.AuditExportAsync(new AuditFilter(Area: "payroll"))).Value!.Count);
        Assert.Equal("hourly", (await admin.PayrollRoleSalariesAsync()).Value!.First(r => r.Role == "employee").PayType);
    }

    [Fact]
    public async Task Per_session_coach_is_paid_for_approved_completed_sessions_only()
    {
        var (gw, admin, _, coach) = await Setup();
        Assert.True((await admin.SetStaffSalaryAsync(coach.UserId, 150m, "per_session")).Ok);
        var member = "u-alex";
        int Add(int h, string status) => gw.OwnerAddSession(new TrainingSession
        { CoachId = coach.UserId, MemberId = member, ScheduledStart = Manila(9, h), ScheduledEnd = Manila(9, h + 1), Status = status }).Id;
        var a = Add(9, "completed"); var b = Add(11, "completed"); Add(13, "no_show"); Add(15, "cancelled");
        var pid = (await admin.PayrollPeriodAsync(Aug)).Value!.PeriodId;
        var recs = (await admin.PayrollRecordsAsync(pid, coach.UserId)).Value!;
        Assert.Equal(2, recs.Count);
        Assert.True((await admin.ReviewPayrollTimeAsync(pid, "training_session", a, "approved")).Ok);
        var row = (await admin.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == coach.UserId);
        Assert.Equal((1m, 1, 150m), (row.Logged, row.Pending, row.BasePay));
        Assert.True((await admin.ReviewPayrollTimeAsync(pid, "training_session", b, "rejected", "Member left early")).Ok);
        Assert.Equal(0, (await admin.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == coach.UserId).Pending);
    }

    [Fact]
    public async Task Corrections_categories_and_gross_then_paid_freezes_rate_and_hours()
    {
        var (gw, admin, desk, _) = await Setup();
        await admin.SetStaffSalaryAsync(desk.UserId, 100m, "hourly");
        gw.OwnerAddShift(desk.UserId, Manila(10, 8), Manila(10, 14));
        var pid = (await admin.PayrollPeriodAsync(Aug)).Value!.PeriodId;
        await admin.ApproveAllPayrollTimeAsync(pid, null);

        Assert.Equal("bad_input", (await admin.AddPayrollHoursAsync(pid, desk.UserId, 1m, " ")).Message);
        var adj = (await admin.AddPayrollHoursAsync(pid, desk.UserId, -10m, "Wrong drawer")).Value!;
        Assert.Equal(0m, (await admin.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == desk.UserId).BasePay);
        Assert.True((await admin.RemovePayrollHoursAsync(adj.AdjustmentId)).Ok);
        await admin.AddPayrollHoursAsync(pid, desk.UserId, 1.5m, "Forgot to open the drawer");
        Assert.Single((await admin.PayrollAdjustmentsAsync(pid, desk.UserId)).Value!);

        Assert.True((await admin.AddPayrollLineAsync(pid, desk.UserId, "bonus", 500m, "Sunday cover", "overtime")).Ok);
        Assert.True((await admin.AddPayrollLineAsync(pid, desk.UserId, "deduction", 200m, "Late", "other")).Ok);
        Assert.Equal("bad_input", (await admin.AddPayrollLineAsync(pid, desk.UserId, "bonus", 5m, "x", "birthday")).Message);
        var row = (await admin.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal((7.5m, 750m, 1250m, 1050m), (row.Payable, row.BasePay, row.Gross, row.Net));
        Assert.Contains((await admin.PayrollLinesAsync(pid, desk.UserId)).Value!, l => l.Category == "overtime");

        var paid = await admin.MarkPayrollPaidAsync(pid);
        Assert.True(paid.Ok, paid.Message);
        Assert.Equal("payroll_paid", (await admin.AddPayrollHoursAsync(pid, desk.UserId, 1m, "Late")).Message);
        await admin.SetStaffSalaryAsync(desk.UserId, 200m, "hourly");
        row = (await admin.PayrollPeriodAsync(Aug)).Value!.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal((100m, 7.5m, 750m), (row.Rate, row.Payable, row.BasePay));
        var salary = (await admin.PayrollLinesAsync(pid, desk.UserId)).Value!.First(l => l.Kind == "salary");
        Assert.Equal(("hourly", 7.5m, 100m), (salary.PayType, salary.Quantity, salary.Rate));
    }
}
