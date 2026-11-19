using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

/// <summary>Owner fixes 2026-11-19 Q2 (0103): 10 wrong passwords lock an account's sign-in for 5 minutes; a right password resets the count.</summary>
public class SignInLockTests
{
    sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]   // Review Focus 2
    public async Task Ten_wrong_passwords_lock_sign_in_for_five_minutes()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var auth = new AuthService(new DemoGateway(clock: clock));
        for (var i = 0; i < 9; i++) Assert.Equal(AppErrorKind.Unauthorized, (await auth.SignInAsync("member@demo.io", "wrong-pass1")).Error);
        var tenth = await auth.SignInAsync("member@demo.io", "wrong-pass1");
        Assert.Equal("account_locked", tenth.Message);
        Assert.Contains("5 minutes", ErrorText.For(tenth.Error, tenth.Message));
        Assert.Equal("account_locked", (await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Message);   // even the right one
        clock.Now = clock.Now.AddMinutes(5).AddSeconds(1);
        Assert.True((await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Ok);
    }

    [Fact]
    public async Task A_right_password_resets_the_count()
    {
        var auth = new AuthService(new DemoGateway());
        for (var i = 0; i < 9; i++) await auth.SignInAsync("member@demo.io", "wrong-pass1");
        Assert.True((await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Ok);
        for (var i = 0; i < 9; i++) await auth.SignInAsync("member@demo.io", "wrong-pass1");
        Assert.True((await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Ok);
    }

    [Fact]   // Review Focus 3: the current-password check on Settings goes through the same door
    public async Task Wrong_current_passwords_on_Settings_count_too()
    {
        var gw = new DemoGateway(); var auth = new AuthService(gw);
        Assert.True((await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Ok);
        for (var i = 0; i < 9; i++) Assert.Equal("wrong_password", (await auth.ChangePasswordAsync("wrong-pass1", "newpass12")).Message);
        Assert.Equal("account_locked", (await auth.ChangePasswordAsync("wrong-pass1", "newpass12")).Message);
    }
}
