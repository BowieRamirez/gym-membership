using GymMembership.Core.Demo;
using GymMembership.Core.Models;
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
        Assert.True((await new AdminService(gw).CreateUserAsync("Dana", "Cruz", "dana@demo.io", "employee", "desk1234")).Ok);
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
    public async Task Admin_resets_a_password_and_the_person_must_pick_a_new_one()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var member = (await admin.UsersAsync()).Value!.First(u => u.Roles.Count == 1 && u.Roles[0] == "member");
        Assert.Equal(AppErrorKind.Validation, (await admin.ResetPasswordAsync(member.UserId, "123")).Error);
        Assert.True((await admin.ResetPasswordAsync(member.UserId, "reset1234")).Ok);

        var auth = new AuthService(gw);
        await gw.SignOutAsync();
        Assert.Equal(AppErrorKind.Unauthorized, (await auth.SignInAsync("member@demo.io", DemoGateway.DemoPassword)).Error);
        Assert.True((await auth.SignInAsync("member@demo.io", "reset1234")).Ok);
        Assert.True((await auth.MustChangePasswordAsync()).Value);
        Assert.Equal(AppErrorKind.Unauthorized, (await admin.ResetPasswordAsync(member.UserId, "hijack1234")).Error);   // a member is not the admin
    }

    [Fact]
    public async Task A_new_sign_up_is_a_guest_until_a_payment_is_verified()
    {
        var gw = new DemoGateway();
        var auth = new AuthService(gw);
        Assert.Equal(AppErrorKind.Validation, (await auth.RegisterAsync("", "", "new@demo.io", "secret12")).Error);
        Assert.Equal(AppErrorKind.Validation, (await auth.RegisterAsync("New", "Person", "new@demo.io", "123")).Error);
        Assert.Equal(AppErrorKind.Validation, (await auth.RegisterAsync("Dupe", "Person", "member@demo.io", "secret12")).Error);
        Assert.True((await auth.RegisterAsync("New", "Person", "new@demo.io", "secret12")).Ok);
        Assert.False((await auth.MustChangePasswordAsync()).Value);   // they chose their own password

        var plans = new MembershipService(gw);
        Assert.Equal(new Access(false, false), (await plans.AccessAsync()).Value);   // guest

        var pay = (await plans.AvailAsync((await plans.ListPackagesAsync()).Value![0].Id)).Value!;
        Assert.Equal(new Access(false, true), (await plans.AccessAsync()).Value);    // paid for, not verified

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, true)).Ok);
        await gw.SignInAsync("new@demo.io", "secret12");                              // own password, not the demo one
        Assert.Equal(new Access(true, false), (await plans.AccessAsync()).Value);    // full member
    }

    [Theory]
    [InlineData("R2D2 Droid")]                  // digits
    [InlineData("Rey!")]                        // symbols
    [InlineData("A")]                           // too short
    [InlineData("Rey  Santos")]                 // double space
    [InlineData("-Rey")]                        // leading separator
    [InlineData("Rey_Santos")]
    public async Task Register_refuses_names_that_are_not_letters(string name)
    {
        var auth = new AuthService(new DemoGateway());
        var r = await auth.RegisterAsync(name, "Santos", "new@demo.io", "secret12");
        Assert.Equal(AppErrorKind.Validation, r.Error);
        Assert.Equal("bad_name", r.Message);
    }

    [Fact]
    public async Task The_owner_cannot_create_an_account_with_a_name_that_is_not_letters()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var r = await new AdminService(gw).CreateUserAsync("Rey", "99", "rey@demo.io", "coach", "temp1234");
        Assert.Equal("bad_name", r.Message);
    }

    [Fact]
    public async Task Register_refuses_a_name_over_60_characters()
    {
        var auth = new AuthService(new DemoGateway());
        Assert.Equal("bad_name", (await auth.RegisterAsync(new string('a', 61), "Santos", "new@demo.io", "secret12")).Message);
        Assert.True((await auth.RegisterAsync(new string('a', 53), "Santos", "new@demo.io", "secret12")).Ok);   // 60 with the space
    }

    [Theory]
    [InlineData("Mary-Ann", "O'Brien")]
    [InlineData("Juan", "dela Cruz")]
    [InlineData("José", "Peña")]
    public async Task Register_accepts_real_names(string first, string last) =>
        Assert.True((await new AuthService(new DemoGateway()).RegisterAsync(first, last, "new@demo.io", "secret12")).Ok);

    [Fact]
    public async Task A_name_typed_with_separate_accent_marks_is_stored_composed_and_marks_the_server_refuses_are_refused_here()
    {
        // the live database's [[:alpha:]] (en_US.UTF-8) refuses a separate combining accent and Devanagari vowel signs (checked 2026-11-08)
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("José", "Peña", "new@demo.io", "secret12")).Ok);
        var me = (await gw.ListAsync<Profile>(p => p.Id == gw.CurrentUserId)).Single();
        Assert.Equal(("José", "Peña"), (me.FirstName, me.LastName));
        Assert.Equal("bad_name", (await new AuthService(new DemoGateway()).RegisterAsync("हिन", "Person", "new@demo.io", "secret12")).Message);
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("a@b")]
    [InlineData("a b@c.com")]
    [InlineData("a@@c.com")]
    public async Task Register_refuses_malformed_emails(string email) =>
        Assert.Equal("bad_email", (await new AuthService(new DemoGateway()).RegisterAsync("New", "Person", email, "secret12")).Message);

    [Fact]
    public async Task Register_caps_the_email_at_100_characters_and_the_part_before_the_at_at_64()
    {
        var auth = new AuthService(new DemoGateway());
        Assert.Equal("bad_email", (await auth.RegisterAsync("New", "Person", new string('a', 65) + "@demo.io", "secret12")).Message);
        Assert.Equal("bad_email", (await auth.RegisterAsync("New", "Person", "a@" + new string('b', 95) + ".com", "secret12")).Message);   // 101 characters
        Assert.True((await auth.RegisterAsync("New", "Person", "a@" + new string('b', 94) + ".com", "secret12")).Ok);                    // 100 characters
        Assert.True((await auth.RegisterAsync("Other", "Person", new string('a', 64) + "@demo.io", "secret12")).Ok);
    }

    [Fact]
    public async Task Register_keeps_the_password_between_8_and_64_characters_with_a_letter_and_a_number()
    {
        // D5: the same rule as Supabase Auth's "letters_digits", which counts only a to z and 0 to 9
        var auth = new AuthService(new DemoGateway());
        foreach (var bad in new[] { "abcde12", "abcdefgh", "12345678", "ññññññ12", "абвгдеж1", new string('x', 64), new string('x', 64) + "1" })
            Assert.Equal("bad_password", (await auth.RegisterAsync("New", "Person", "new@demo.io", bad)).Message);
        Assert.True((await auth.RegisterAsync("New", "Person", "new@demo.io", "abcdefg1")).Ok);
        Assert.True((await auth.RegisterAsync("Other", "Person", "other@demo.io", new string('x', 63) + "1")).Ok);
    }

    [Fact]
    public async Task Every_way_to_set_a_password_uses_the_same_rule()
    {
        // D5: change, the staff temporary password and the staff reset refused "abcdefgh" before; they checked only 6 characters
        var gw = await As("admin@demo.io");
        var auth = new AuthService(gw);
        Assert.Equal("bad_password", (await auth.ChangePasswordAsync(DemoGateway.DemoPassword, "abcdefgh")).Message);
        Assert.Equal("bad_password", (await auth.ChangePasswordAsync(DemoGateway.DemoPassword, new string('a', 64) + "1")).Message);
        var admin = new AdminService(gw);
        Assert.Equal("bad_password", (await admin.CreateUserAsync("Xena", "Cruz", "x@demo.io", "member", "abcdefgh")).Message);
        Assert.Equal("bad_email", (await admin.CreateUserAsync("Xena", "Cruz", "x@demo", "member", "abcdefg1")).Message);
        var member = (await admin.UsersAsync()).Value!.First(u => u.Roles.Count == 1 && u.Roles[0] == "member");
        Assert.Equal("bad_password", (await admin.ResetPasswordAsync(member.UserId, "12345678")).Message);
        Assert.True((await admin.ResetPasswordAsync(member.UserId, "temp1234")).Ok);
    }

    [Fact]
    public async Task The_demo_refuses_a_weak_password_even_when_the_app_check_is_skipped()
    {
        // the server (Supabase Auth, the Edge Functions) is the real check; the demo stands in for it
        await Assert.ThrowsAnyAsync<Exception>(() => new DemoGateway().SignUpAsync("weak@demo.io", "abcdefgh", "Weak", "Person"));
        var gw = await As("admin@demo.io");
        await Assert.ThrowsAnyAsync<Exception>(() => gw.InvokeFunctionAsync("create-user", new()
            { ["first_name"] = "Weak", ["last_name"] = "Person", ["email"] = "weak@demo.io", ["role"] = "member", ["password"] = "abcdefgh" }));
    }

    [Theory]
    [InlineData("R2D2 Droid", "RD Droid")]
    [InlineData("Rey!_Santos", "ReySantos")]
    [InlineData("Mary-Ann O'Brien", "Mary-Ann O'Brien")]
    [InlineData("José Peña", "José Peña")]
    [InlineData("12345", "")]
    public void The_name_field_drops_anything_that_is_not_a_letter_as_you_type(string typed, string kept) =>
        Assert.Equal(kept, AccountRules.KeepNameChars(typed));
}
