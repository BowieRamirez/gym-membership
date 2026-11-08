using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 11 (D13) in demo mode: first and last name (full_name built from them), confirming an email with a code sent to it,
// and resetting a forgotten password with a code. The demo's code is always DemoGateway.DemoCode.
public class NamesAndEmailTests
{
    static async Task<DemoGateway> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    static Profile Me(DemoGateway gw) => gw.ListAsync<Profile>(p => p.Id == gw.CurrentUserId).Result.Single();

    [Fact]
    public async Task Register_stores_first_and_last_name_and_builds_the_full_name()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync(" Mary Ann ", "Santos", "mary@demo.io", "secret12")).Ok);
        var me = Me(gw);
        Assert.Equal(("Mary Ann", "Santos", "Mary Ann Santos"), (me.FirstName, me.LastName, me.FullName));
    }

    [Theory]
    [InlineData("", "Santos")]
    [InlineData("Mary", "")]
    [InlineData("R2D2", "Santos")]
    [InlineData("Mary", "Santos!")]
    [InlineData("A", "Santos")]
    public async Task Register_needs_both_names_in_letters(string first, string last) =>
        Assert.Equal("bad_name", (await new AuthService(new DemoGateway()).RegisterAsync(first, last, "new@demo.io", "secret12")).Message);

    [Fact]
    public async Task First_and_last_name_together_stay_within_60_characters()
    {
        var auth = new AuthService(new DemoGateway());
        Assert.Equal("bad_name", (await auth.RegisterAsync(new string('a', 30), new string('b', 30), "new@demo.io", "secret12")).Message);   // 61 with the space
        Assert.True((await auth.RegisterAsync(new string('a', 30), new string('b', 29), "new@demo.io", "secret12")).Ok);
    }

    [Fact]
    public void Old_names_split_at_the_first_space()
    {
        Assert.Equal(("Juan", "dela Cruz"), NameParts.Split("Juan dela Cruz"));
        Assert.Equal(("Madonna", ""), NameParts.Split(" Madonna "));
        Assert.Equal("Juan dela Cruz", NameParts.Join("Juan", "dela Cruz"));
    }

    [Fact]
    public async Task Seeded_demo_people_have_their_names_split()
    {
        var me = Me(await As("member@demo.io"));
        Assert.Equal(("Alex", "Tan", "Alex Tan"), (me.FirstName, me.LastName, me.FullName));
    }

    [Fact]
    public async Task Only_a_system_admin_corrects_a_name_and_the_full_name_follows()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        Assert.Equal("bad_name", (await admin.CorrectNameAsync("u-coach1", "Ana", "", "Typo")).Message);
        Assert.Equal("bad_name", (await admin.CorrectNameAsync("u-coach1", "Ana1", "Reyes", "Typo")).Message);
        Assert.Equal("bad_correction_reason", (await admin.CorrectNameAsync("u-coach1", "Ana", "Reyes", "   ")).Message);
        Assert.Equal("bad_correction_reason", (await admin.CorrectNameAsync("u-coach1", "Ana", "Reyes", new string('x', 501))).Message);

        Assert.True((await admin.CorrectNameAsync("u-coach1", " Ana Maria ", " Reyes-Cruz ", " Typo at sign-up ")).Ok);
        var coach = (await gw.ListAsync<Profile>(p => p.Id == "u-coach1")).Single();
        Assert.Equal(("Ana Maria Reyes-Cruz", "Ana Maria", "Reyes-Cruz"), (coach.FullName, coach.FirstName, coach.LastName));
        var log = (await gw.ListAsync<AuditEntry>()).Last(a => a.Action == "user.correct_name");
        Assert.Equal(("u-coach1", "u-admin"), (log.EntityId, log.ActorId));
        Assert.Equal("Typo at sign-up", log.Details!["reason"]?.ToString());

        Assert.True((await admin.CorrectNameAsync("u-admin", "Dana", "Reyes", "My own typo")).Ok);   // otherwise a lone system admin's typo is permanent
    }

    [Theory]
    [InlineData("subadmin@demo.io")]
    [InlineData("employee@demo.io")]
    [InlineData("coach@demo.io")]
    [InlineData("member@demo.io")]
    public async Task Nobody_else_can_correct_a_name(string email)
    {
        var gw = await As(email);
        var result = await new AdminService(gw).CorrectNameAsync("u-coach1", "New", "Name", "Reason");
        Assert.False(result.Ok);
        Assert.Equal(AppErrorKind.Unauthorized, result.Error);
    }

    [Fact]
    public async Task An_archived_account_cannot_be_corrected()
    {
        var gw = await As("admin@demo.io");
        await new AdminService(gw).SetActiveAsync("u-coach1", false);
        Assert.Equal("record_archived", (await new AdminService(gw).CorrectNameAsync("u-coach1", "New", "Name", "Reason")).Message);
    }

    [Fact]
    public async Task The_users_page_corrects_a_name_for_a_system_admin_only()
    {
        var gw = await As("admin@demo.io");
        var vm = new UsersViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        var coach = vm.Users.Single(u => u.UserId == "u-coach1");
        Assert.True(vm.CanCorrectName(coach));
        vm.StartCorrectNameCommand.Execute(coach);
        Assert.Equal((coach.FirstName, coach.LastName, ""), (vm.CorrectFirstName, vm.CorrectLastName, vm.CorrectReason));

        vm.CorrectFirstName = "Rico"; vm.CorrectLastName = "Dalisay";
        await vm.ConfirmCorrectNameCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);   // no reason yet: the panel stays open
        Assert.NotNull(vm.Correcting);

        vm.CorrectReason = "Typo at sign-up";
        await vm.ConfirmCorrectNameCommand.ExecuteAsync(null);
        Assert.Null(vm.Correcting);
        Assert.Equal("Name corrected to Rico Dalisay.", vm.Notice);
        Assert.Contains(vm.Users, u => u.UserId == "u-coach1" && u.FirstName == "Rico" && u.LastName == "Dalisay");

        var sub = await As("subadmin@demo.io");
        var subVm = new UsersViewModel(new AdminService(sub));
        await subVm.OnAppearingAsync();
        Assert.False(subVm.CanCorrectName(subVm.Users.First(u => u.UserId == "u-coach1")));
    }

    [Fact]
    public async Task The_owner_adds_a_user_with_first_and_last_name()
    {
        var gw = await As("admin@demo.io");
        Assert.True((await new AdminService(gw).CreateUserAsync("Dana", "dela Cruz", "dana@demo.io", "employee", "desk1234")).Ok);
        Assert.Equal("bad_name", (await new AdminService(gw).CreateUserAsync("Rey", "99", "rey@demo.io", "coach", "temp1234")).Message);
        await gw.SignInAsync("dana@demo.io", "desk1234");
        Assert.Equal(("Dana", "dela Cruz", "Dana dela Cruz"), (Me(gw).FirstName, Me(gw).LastName, Me(gw).FullName));
    }

    [Fact]
    public async Task A_member_confirms_their_email_with_the_code_sent_to_it()
    {
        var gw = await As("member@demo.io");
        var auth = new AuthService(gw);
        Assert.Equal(new EmailStatus("member@demo.io", false), (await auth.EmailStatusAsync()).Value);

        Assert.True((await auth.SendEmailCodeAsync("member@demo.io")).Ok);
        Assert.Equal("bad_code", (await auth.ConfirmEmailAsync("member@demo.io", "000000")).Message);
        Assert.Equal("bad_code", (await auth.ConfirmEmailAsync("member@demo.io", "12ab")).Message);
        Assert.True((await auth.ConfirmEmailAsync("member@demo.io", DemoGateway.DemoCode)).Ok);
        Assert.Equal(new EmailStatus("member@demo.io", true), (await auth.EmailStatusAsync()).Value);
    }

    [Fact]
    public async Task A_code_cannot_be_used_twice()
    {
        var gw = await As("member@demo.io");
        var auth = new AuthService(gw);
        await auth.SendEmailCodeAsync("member@demo.io");
        Assert.True((await auth.ConfirmEmailAsync("member@demo.io", DemoGateway.DemoCode)).Ok);
        Assert.Equal("bad_code", (await auth.ConfirmEmailAsync("member@demo.io", DemoGateway.DemoCode)).Message);
    }

    [Fact]
    public async Task A_new_email_becomes_the_sign_in_email_and_must_be_confirmed()
    {
        var gw = await As("member@demo.io");
        var auth = new AuthService(gw);
        await auth.SendEmailCodeAsync("member@demo.io");
        await auth.ConfirmEmailAsync("member@demo.io", DemoGateway.DemoCode);

        Assert.Equal("email_taken", (await auth.SendEmailCodeAsync("bea@demo.io")).Message);
        Assert.Equal("bad_email", (await auth.SendEmailCodeAsync("not an email")).Message);
        Assert.True((await auth.SendEmailCodeAsync("alex.real@mail.com")).Ok);
        Assert.Equal(new EmailStatus("alex.real@mail.com", false), (await auth.EmailStatusAsync()).Value);   // the old confirmation no longer counts
        Assert.True((await auth.ConfirmEmailAsync("alex.real@mail.com", DemoGateway.DemoCode)).Ok);
        Assert.True((await auth.EmailStatusAsync()).Value!.Confirmed);

        await gw.SignOutAsync();
        Assert.True((await auth.SignInAsync("alex.real@mail.com", DemoGateway.DemoPassword)).Ok);
        Assert.False((await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Ok);
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_with_a_code_and_never_reveals_who_has_an_account()
    {
        var gw = new DemoGateway();
        var auth = new AuthService(gw);
        Assert.True((await auth.SendResetCodeAsync("nobody@demo.io")).Ok);   // same answer as a real account
        Assert.Equal("bad_email", (await auth.SendResetCodeAsync("nobody")).Message);
        Assert.True((await auth.SendResetCodeAsync("member@demo.io")).Ok);

        Assert.Equal("bad_code", (await auth.ResetPasswordAsync("member@demo.io", "999999", "fresh123")).Message);
        Assert.Equal("bad_password", (await auth.ResetPasswordAsync("member@demo.io", DemoGateway.DemoCode, "123")).Message);
        Assert.True((await auth.ResetPasswordAsync("member@demo.io", DemoGateway.DemoCode, "fresh123")).Ok);
        Assert.Null(gw.CurrentUserId);   // signed out: they sign in with the new password

        Assert.False((await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Ok);
        Assert.True((await auth.SignInAsync("member@demo.io", "fresh123")).Ok);
        Assert.False((await auth.MustChangePasswordAsync()).Value);
        Assert.True((await auth.EmailStatusAsync()).Value!.Confirmed);   // the reset code also proved the email
    }

    [Fact]
    public async Task A_reset_code_for_an_unknown_email_is_refused_like_a_wrong_code() =>
        Assert.Equal("bad_code", (await new AuthService(new DemoGateway()).ResetPasswordAsync("nobody@demo.io", DemoGateway.DemoCode, "fresh123")).Message);

    [Fact]
    public async Task An_admin_temporary_password_still_works_alongside_codes()
    {
        var gw = await As("admin@demo.io");
        var member = (await new AdminService(gw).UsersAsync()).Value!.First(u => u.Roles.SequenceEqual(["member"]));
        Assert.True((await new AdminService(gw).ResetPasswordAsync(member.UserId, "temp1234")).Ok);
    }

    // ---------- view models ----------
    [Fact]
    public async Task The_register_page_sends_both_names()
    {
        var gw = new DemoGateway();
        var vm = new RegisterViewModel(new AuthService(gw), new NullNavigator())
        { FirstName = "Mary", LastName = "Santos", Email = "mary@demo.io", Password = "secret12", ConfirmPassword = "secret12" };
        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Mary Santos", Me(gw).FullName);
    }

    [Fact]
    public async Task Settings_shows_the_name_and_offers_no_way_to_change_it()
    {
        var gw = await As("member@demo.io");
        var vm = Settings(gw);
        await vm.OnAppearingAsync();
        Assert.Equal(("Alex", "Tan"), (vm.FirstName, vm.LastName));
        Assert.Null(typeof(SettingsViewModel).GetProperty("SaveNameCommand"));
        Assert.Null(typeof(IProfileService).GetMethod("SetNameAsync"));
    }

    [Fact]
    public async Task Settings_walks_through_sending_and_entering_an_email_code()
    {
        var gw = await As("member@demo.io");
        var vm = Settings(gw);
        await vm.OnAppearingAsync();
        Assert.Equal("member@demo.io", vm.EmailAddress);
        Assert.False(vm.EmailConfirmed);
        Assert.False(vm.CodeSent);

        await vm.SendEmailCodeCommand.ExecuteAsync(null);
        Assert.True(vm.CodeSent);
        Assert.Contains("member@demo.io", vm.Notice);
        Assert.Contains(DemoGateway.DemoCode, vm.Notice);   // the demo shows its code, since it sends no email

        vm.EmailCode = "000000";
        await vm.ConfirmEmailCommand.ExecuteAsync(null);
        Assert.Equal(ErrorText.For(AppErrorKind.Validation, "bad_code"), vm.ErrorMessage);
        Assert.False(vm.EmailConfirmed);

        vm.EmailCode = DemoGateway.DemoCode;
        await vm.ConfirmEmailCommand.ExecuteAsync(null);
        Assert.True(vm.EmailConfirmed);
        Assert.False(vm.CodeSent);
        Assert.Equal("Email confirmed.", vm.Notice);
    }

    [Fact]
    public async Task The_login_page_resets_a_forgotten_password_in_two_steps()
    {
        var gw = new DemoGateway();
        var vm = new LoginViewModel(new AuthService(gw), new NullNavigator(), gw);
        vm.ForgotPasswordCommand.Execute(null);
        Assert.True(vm.IsResetting);

        Assert.True(vm.IsResetStep1);
        vm.ResetEmail = "member@demo.io";
        await vm.SendResetCodeCommand.ExecuteAsync(null);
        Assert.True(vm.ResetCodeSent);
        Assert.True(vm.IsResetStep2);
        Assert.False(vm.CanResend);   // the next code waits 30 seconds
        Assert.Equal("Resend code in 0:30", vm.ResendText);
        Assert.StartsWith("If an account uses member@demo.io, we sent a 6-digit code to it.", vm.Notice);

        vm.ResetCode = DemoGateway.DemoCode; vm.NewPassword = "fresh123"; vm.ConfirmNewPassword = "fresh124";
        await vm.ResetPasswordCommand.ExecuteAsync(null);
        Assert.Equal("The new passwords don't match.", vm.ErrorMessage);

        vm.ConfirmNewPassword = "fresh123";
        await vm.ResetPasswordCommand.ExecuteAsync(null);
        Assert.False(vm.IsResetting);
        Assert.Equal("member@demo.io", vm.Email);
        Assert.Equal("Password changed. Sign in with your new password.", vm.Notice);
        Assert.True((await new AuthService(gw).SignInAsync("member@demo.io", "fresh123")).Ok);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 1)]          // too short
    [InlineData("abcdefgh", 1)]     // length only
    [InlineData("abcdefg1", 2)]     // the two required checks
    [InlineData("Abcdefg1", 3)]
    [InlineData("Abcdefghij12", 4)]
    public void Password_strength_counts_the_checklist(string password, int strength) =>
        Assert.Equal(strength, AccountRules.PasswordStrength(password));

    static SettingsViewModel Settings(DemoGateway gw) =>
        new(new SettingsService(gw), new AuthService(gw), new NullNavigator(), new ProfileService(gw), new DemoPhotoPicker(), gw);

    internal sealed class NullNavigator : IAppNavigator
    {
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() { }
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToMembership() { }
        public void ApplyTheme(string theme) { }
    }
}
