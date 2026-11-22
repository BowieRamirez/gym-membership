using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class PayrollTests
{
    static async Task<(DemoGateway Gw, AdminService Admin)> SignIn(string email = "admin@demo.io")
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new AdminService(gw));
    }

    [Fact]
    public async Task Front_desk_coach_and_member_cannot_access_payroll()
    {
        foreach (var email in new[] { "employee@demo.io", "coach@demo.io", "member@demo.io" })
        {
            var (_, svc) = await SignIn(email);
            Assert.Equal(AppErrorKind.Unauthorized, (await svc.PayrollStaffAsync()).Error);
            Assert.Equal(AppErrorKind.Unauthorized, (await svc.PayrollPeriodAsync(new DateOnly(2026, 10, 1))).Error);
        }
    }

    [Fact]
    public async Task Admin_cannot_set_own_salary_and_subadmin_appears_only_after_salary_set()
    {
        var (gw, admin) = await SignIn("admin@demo.io");
        var selfErr = await admin.SetStaffSalaryAsync(gw.CurrentUserId!, 12000m);
        Assert.False(selfErr.Ok);
        Assert.Equal("own_salary", selfErr.Message);

        var staff = (await admin.PayrollStaffAsync()).Value!;
        Assert.Contains(staff, s => s.Role == "employee");
        Assert.Contains(staff, s => s.Role == "coach");
        Assert.DoesNotContain(staff, s => s.Role is "admin" or "subadmin");

        var sub = (await admin.UsersAsync()).Value!.First(u => u.Roles.Contains("subadmin"));
        Assert.True((await admin.SetStaffSalaryAsync(sub.UserId, 15000m)).Ok);
        staff = (await admin.PayrollStaffAsync()).Value!;
        Assert.Contains(staff, s => s.UserId == sub.UserId && s.Amount == 15000m);
    }

    [Fact]
    public async Task Draft_follows_salary_changes_and_paid_locks_the_period_and_freezes_salaries()
    {
        var (gw, admin) = await SignIn("admin@demo.io");
        var staff = (await admin.PayrollStaffAsync()).Value!;
        var desk = staff.First(s => s.Role == "employee");

        Assert.True((await admin.SetStaffSalaryAsync(desk.UserId, 8000m)).Ok);
        var period = (await admin.PayrollPeriodAsync(new DateOnly(2026, 10, 1))).Value!;
        Assert.Equal(new DateOnly(2026, 10, 15), period.EndsOn);
        Assert.False(period.IsPaid);

        var bonus = (await admin.AddPayrollLineAsync(period.PeriodId, desk.UserId, "bonus", 500m, "Holiday cover")).Value!;
        var bigDed = (await admin.AddPayrollLineAsync(period.PeriodId, desk.UserId, "deduction", 9000m, "Too big")).Value!;
        var negErr = await admin.MarkPayrollPaidAsync(period.PeriodId);
        Assert.False(negErr.Ok);
        Assert.Contains("net_below_zero:", negErr.Message);
        Assert.Contains(desk.FullName, ErrorText.For(negErr.Error, negErr.Message));

        Assert.True((await admin.RemovePayrollLineAsync(bigDed.LineId)).Ok);
        Assert.True((await admin.SetStaffSalaryAsync(desk.UserId, 8500m)).Ok);

        period = (await admin.PayrollPeriodAsync(new DateOnly(2026, 10, 1))).Value!;
        var deskRow = period.Rows.First(r => r.UserId == desk.UserId);
        Assert.Equal((8500m, 500m, 9000m), (deskRow.Salary, deskRow.Bonuses, deskRow.Net));

        var paid = (await admin.MarkPayrollPaidAsync(period.PeriodId)).Value!;
        Assert.True(paid.IsPaid);

        Assert.Equal("payroll_paid", (await admin.MarkPayrollPaidAsync(period.PeriodId)).Message);
        Assert.Equal("payroll_paid", (await admin.AddPayrollLineAsync(period.PeriodId, desk.UserId, "bonus", 100m, "Late")).Message);
        Assert.Equal("payroll_paid", (await admin.RemovePayrollLineAsync(bonus.LineId)).Message);

        // Salary change after paid does not change the paid period, only the next draft
        Assert.True((await admin.SetStaffSalaryAsync(desk.UserId, 9500m)).Ok);
        var paidAgain = (await admin.PayrollPeriodAsync(new DateOnly(2026, 10, 1))).Value!;
        Assert.Equal(8500m, paidAgain.Rows.First(r => r.UserId == desk.UserId).Salary);

        var nextDraft = (await admin.PayrollPeriodAsync(new DateOnly(2026, 10, 16))).Value!;
        Assert.Equal(9500m, nextDraft.Rows.First(r => r.UserId == desk.UserId).Salary);
    }

    [Fact]
    public async Task Period_not_ended_refuses_mark_paid_for_future_period()
    {
        var (_, admin) = await SignIn("admin@demo.io");
        var futureStart = new DateOnly(DateTime.UtcNow.Year + 1, 1, 1);
        var period = (await admin.PayrollPeriodAsync(futureStart)).Value!;
        var res = await admin.MarkPayrollPaidAsync(period.PeriodId);
        Assert.False(res.Ok);
        Assert.Equal("period_not_ended", res.Message);
    }
}
