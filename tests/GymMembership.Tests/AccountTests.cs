using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

public class AccountTests
{
    static async Task<DemoGateway> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    [Fact]
    public async Task Admin_adds_an_employee_who_lands_in_the_front_desk_area()
    {
        var gw = await As("admin@demo.io");
        Assert.True((await new AdminService(gw).CreateUserAsync("Dana Cruz", "dana@demo.io", "employee", "desk1234")).Ok);
        Assert.Equal(AppRole.Employee, (await new AuthService(gw).SignInAsync("dana@demo.io", "desk1234")).Value);
    }

    [Fact]
    public async Task Changing_your_password_needs_the_current_one_and_replaces_it()
    {
        var gw = await As("member@demo.io");
        var auth = new AuthService(gw);
        Assert.Equal(AppErrorKind.Validation, (await auth.ChangePasswordAsync("nope", "newpass1")).Error);
        Assert.Equal(AppErrorKind.Validation, (await auth.ChangePasswordAsync(DemoGateway.DemoPassword, "123")).Error);
        Assert.True((await auth.ChangePasswordAsync(DemoGateway.DemoPassword, "newpass1")).Ok);

        Assert.Equal(AppErrorKind.Unauthorized, (await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Error);
        Assert.True((await auth.SignInAsync("member@demo.io", "newpass1")).Ok);
    }

    [Fact]
    public async Task Admin_resets_a_password_and_nobody_else_can()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var member = (await admin.UsersAsync()).Value!.First(u => u.Roles.Count == 1 && u.Roles[0] == "member");
        Assert.Equal(AppErrorKind.Validation, (await admin.ResetPasswordAsync(member.UserId, "123")).Error);
        Assert.True((await admin.ResetPasswordAsync(member.UserId, "reset1234")).Ok);

        await gw.SignOutAsync();
        Assert.Equal(AppErrorKind.Unauthorized, (await new AuthService(gw).SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Error);
        Assert.True((await new AuthService(gw).SignInAsync("member@demo.io", "reset1234")).Ok);
        Assert.Equal(AppErrorKind.Unauthorized, (await admin.ResetPasswordAsync(member.UserId, "hijack1234")).Error);
    }

    [Fact]
    public async Task A_new_sign_up_is_a_guest_until_a_payment_is_verified()
    {
        var gw = new DemoGateway();
        var auth = new AuthService(gw);
        Assert.Equal(AppErrorKind.Validation, (await auth.RegisterAsync("", "new@demo.io", "secret12")).Error);
        Assert.Equal(AppErrorKind.Validation, (await auth.RegisterAsync("New Person", "new@demo.io", "123")).Error);
        Assert.Equal(AppErrorKind.Validation, (await auth.RegisterAsync("Dupe", "member@demo.io", "secret12")).Error);
        Assert.True((await auth.RegisterAsync("New Person", "new@demo.io", "secret12")).Ok);

        var plans = new MembershipService(gw);
        Assert.Equal(new Access(false, false), (await plans.AccessAsync()).Value);   // guest

        var pay = (await plans.AvailAsync((await plans.ListPackagesAsync()).Value![0].Id)).Value;
        Assert.Equal(new Access(false, true), (await plans.AccessAsync()).Value);    // paid for, not verified

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay, true)).Ok);
        await gw.SignInAsync("new@demo.io", "secret12");                              // own password, not the demo one
        Assert.Equal(new Access(true, false), (await plans.AccessAsync()).Value);    // full member
    }
}
