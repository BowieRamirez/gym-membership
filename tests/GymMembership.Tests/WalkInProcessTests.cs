using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Cashier plan Y2, Y3 (owner's walk-in document, 2026-11-16): a Day pass needs no account or name; a customer who wants a
/// membership is searched for first, then registered at the Cashier as a member if nobody matches.</summary>
public class WalkInProcessTests
{
    static async Task<(DemoGateway Gw, CashierViewModel Vm)> Open()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    static async Task<Payment> Last(DemoGateway gw) => (await gw.ListAsync<Payment>()).OrderByDescending(p => p.Id).First();

    [Fact]
    public async Task The_cashier_sells_a_Day_pass_not_a_walk_in() => Assert.Contains("Day pass", (await Open()).Vm.Kinds);

    [Fact]
    public async Task A_Day_pass_needs_no_name()
    {
        var (gw, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        Assert.True(vm.RecordCommand.CanExecute(null));
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var pay = await Last(gw);
        Assert.Equal((PayKind.WalkIn, "Day pass", Status.Verified), (pay.Kind, pay.WalkInName, pay.Status));
    }

    [Fact]
    public async Task A_Day_pass_for_a_member_found_by_search_carries_their_name()
    {
        var (gw, vm) = await Open();
        vm.PickKindCommand.Execute("Day pass");
        vm.Search = "carlo"; await vm.SuggestNowAsync();
        vm.PickMemberCommand.Execute(vm.Hits.Single());
        Assert.Equal("Carlo Diaz", vm.OrderWho);
        await vm.RecordCommand.ExecuteAsync(null);
        var pay = await Last(gw); Assert.Equal((PayKind.Membership, "u-carlo"), (pay.Kind, pay.MemberId));   // 0098: their Day pass is the package
    }

    [Fact]   // final review: the member chosen for the last sale must not name the next anonymous Day pass
    public async Task After_a_sale_the_next_Day_pass_starts_anonymous()
    {
        var (gw, vm) = await Open();
        vm.Search = "carlo"; await vm.SuggestNowAsync();
        vm.PickMemberCommand.Execute(vm.Hits.Single());
        vm.PickItemCommand.Execute(vm.Options[0]);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        vm.PickKindCommand.Execute("Day pass");
        Assert.Equal("Day pass, no name", vm.OrderWho);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Equal("Day pass", (await Last(gw)).WalkInName);
    }

    [Fact]   // final review: Customer left cancels the pending desk payment, so the member can pay another way at once
    public async Task Customer_left_cancels_the_desk_payment()
    {
        var (gw, _) = await Open();
        var svc = new PaymentStaffService(gw);
        var bea = (await svc.ItemsAsync(PayKind.Membership, "u-carlo")).Value![0];
        var pending = (await svc.StartDeskOnlineAsync(PayKind.Membership, "u-carlo", bea.Id, null, null)).Value!;
        Assert.True((await svc.VerifyAsync(pending.Id, false, "Customer left at the desk")).Ok);
        Assert.True((await svc.RecordCounterAsync(PayKind.Membership, PayMethod.Cash, "u-carlo", bea.Id, null, null, null)).Ok);
    }

    [Fact]   // owner, 2026-11-16: leaving the Cashier lost the Check payment button for a desk payment still waiting for Xendit
    public async Task Reopening_the_Cashier_picks_up_your_desk_payment_still_waiting_for_Xendit()
    {
        var (gw, _) = await Open();
        var svc = new PaymentStaffService(gw);
        var item = (await svc.ItemsAsync(PayKind.Membership, "u-carlo")).Value![0];
        var pending = (await svc.StartDeskOnlineAsync(PayKind.Membership, "u-carlo", item.Id, null, null)).Value!;
        var again = new CashierViewModel(svc, new AttendanceService(gw));
        await again.OnAppearingAsync();
        Assert.Equal(pending.Id, again.Waiting?.Id);
        await again.CheckPaymentCommand.ExecuteAsync(null);   // demo: not paid until the checkout is opened
        Assert.NotNull(again.Waiting);
    }

    [Fact]   // owner, 2026-11-25: Membership is only the register form; Renewal and Day pass search, with no register
    public async Task Membership_registers_while_Renewal_and_Day_pass_search()
    {
        var (_, vm) = await Open();
        Assert.Equal((false, true), (vm.ShowsSearch, vm.ShowsRegisterForm));
        foreach (var kind in new[] { "Renewal", "Day pass" })
        {
            vm.PickKindCommand.Execute(kind);
            Assert.Equal((true, false), (vm.ShowsSearch, vm.ShowsRegisterForm));
        }
    }

    [Fact]   // owner, 2026-11-25: Membership registers the account, its package and the payment in one go
    public async Task Membership_registers_the_account_with_its_package_and_payment_in_one_go()
    {
        var (gw, vm) = await Open();
        (vm.NewFirstName, vm.NewLastName, vm.NewEmail, vm.NewPhone) = ("Nora", "Cruz", "nora@demo.io", "0918 222 3344");
        Assert.False(vm.RecordCommand.CanExecute(null));   // a package first
        await WaitForOptions(vm);
        vm.PickItemCommand.Execute(vm.Options[0]);
        Assert.True(vm.RecordCommand.CanExecute(null));
        Assert.Equal("New member: Nora Cruz", vm.OrderWho);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("Payment complete", vm.Notice);
        Assert.Contains("Temporary password", vm.Notice);
        var profile = (await gw.ListAsync<Profile>()).Single(p => p.Username == "nora@demo.io");
        Assert.Equal(("0918 222 3344", true), (profile.Phone, profile.MustChangePassword));
        Assert.Single(await gw.ListAsync<Member>(), m => m.UserId == profile.Id);
        var pay = await Last(gw);
        Assert.Equal((PayKind.Membership, profile.Id, Status.Verified), (pay.Kind, pay.MemberId, pay.Status));
        Assert.True(vm.ShowsRegisterForm);   // the next customer starts clean
        Assert.Equal("", vm.NewEmail);
    }

    [Fact]
    public async Task A_new_member_can_pay_online_and_keeps_the_temporary_password_on_screen()
    {
        var (gw, vm) = await Open();
        (vm.NewFirstName, vm.NewLastName, vm.NewEmail) = ("Lia", "Santos", "lia@demo.io");
        await WaitForOptions(vm);
        vm.PickItemCommand.Execute(vm.Options[0]);
        vm.PickMethodCommand.Execute(CashierViewModel.Online);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        // the demo's checkout is paid as soon as it opens, so the sale completes here
        Assert.Contains("Payment complete", vm.Notice);
        Assert.Contains("Temporary password", vm.Notice);
        var lia = (await gw.ListAsync<Profile>()).Single(p => p.Username == "lia@demo.io");
        var pay = await Last(gw);
        Assert.Equal((PayKind.Membership, lia.Id, PayMethod.Qr), (pay.Kind, pay.MemberId, pay.Method));
    }

    static async Task WaitForOptions(CashierViewModel vm)
    {
        for (var i = 0; i < 50 && vm.Options.Count == 0; i++) await Task.Delay(20);
        Assert.NotEmpty(vm.Options);
    }

    [Fact]
    public async Task Registering_an_email_already_in_use_is_refused_and_nothing_is_paid()
    {
        var (gw, vm) = await Open();
        var before = (await gw.ListAsync<Payment>()).Count;
        (vm.NewFirstName, vm.NewLastName, vm.NewEmail) = ("Bea", "Lim", "bea@demo.io");
        await WaitForOptions(vm);
        vm.PickItemCommand.Execute(vm.Options[0]);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.ShowsRegisterForm);
        Assert.Equal(before, (await gw.ListAsync<Payment>()).Count);
    }

    [Fact]
    public async Task The_front_desk_can_create_members_but_never_staff_or_coaches()
    {
        var (gw, _) = await Open();
        await Assert.ThrowsAnyAsync<Exception>(() => gw.InvokeFunctionAsync("create-user", new()
            { ["first_name"] = "Coach", ["last_name"] = "Sneaky", ["email"] = "sneaky@demo.io", ["role"] = "coach", ["password"] = "Temp12345" }));
        await Assert.ThrowsAnyAsync<Exception>(() => gw.InvokeFunctionAsync("reset-password", new() { ["user_id"] = "u-bea", ["password"] = "Temp12345" }));
    }

    [Fact]
    public void A_temporary_password_meets_the_password_rules()
    {
        for (var i = 0; i < 50; i++)
        {
            var p = TempPassword.New();
            Assert.True(AccountRules.IsPassword(p), p);
            Assert.Equal(10, p.Length);
        }
    }
}
