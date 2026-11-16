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
        vm.Search = "carlo"; await vm.FindCommand.ExecuteAsync(null);
        vm.PickMemberCommand.Execute(vm.Hits.Single());
        Assert.Equal("Carlo Diaz", vm.OrderWho);
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Equal("Carlo Diaz", (await Last(gw)).WalkInName);
    }

    [Fact]   // final review: the member chosen for the last sale must not name the next anonymous Day pass
    public async Task After_a_sale_the_next_Day_pass_starts_anonymous()
    {
        var (gw, vm) = await Open();
        vm.Search = "carlo"; await vm.FindCommand.ExecuteAsync(null);
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

    [Fact]   // owner, 2026-11-16: Find shows only the table; Register new member shows only its form
    public async Task Find_and_register_show_one_at_a_time()
    {
        var (_, vm) = await Open();
        vm.Search = "a"; await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal((true, false), (vm.ShowHits, vm.IsRegistering));
        vm.StartRegisterCommand.Execute(null);
        Assert.Equal((false, true, false), (vm.ShowHits, vm.IsRegistering, vm.ShowNoMatch));
        await vm.FindCommand.ExecuteAsync(null);
        Assert.Equal((true, false), (vm.ShowHits, vm.IsRegistering));
        vm.Search = "zzz"; await vm.FindCommand.ExecuteAsync(null);
        Assert.True(vm.ShowNoMatch);
        vm.StartRegisterCommand.Execute(null);
        Assert.False(vm.ShowNoMatch);
    }

    [Fact]
    public async Task Nobody_found_then_register_them_as_a_member_and_they_are_chosen()
    {
        var (gw, vm) = await Open();
        vm.Search = "nora"; await vm.FindCommand.ExecuteAsync(null);
        Assert.True(vm.NoMatch);
        vm.StartRegisterCommand.Execute(null);
        Assert.True(vm.IsRegistering);
        (vm.NewFirstName, vm.NewLastName, vm.NewEmail, vm.NewPhone) = ("Nora", "Cruz", "nora@demo.io", "0918 222 3344");
        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsRegistering);
        Assert.Equal("Nora Cruz", vm.Member?.Username);
        Assert.Contains("Temporary password", vm.Notice);
        var profile = (await gw.ListAsync<Profile>()).Single(p => p.Username == "nora@demo.io");
        Assert.Equal(("0918 222 3344", true), (profile.Phone, profile.MustChangePassword));
        Assert.Single(await gw.ListAsync<Member>(), m => m.UserId == profile.Id);
    }

    [Fact]
    public async Task Registering_an_email_already_in_use_is_refused_and_says_so()
    {
        var (_, vm) = await Open();
        vm.StartRegisterCommand.Execute(null);
        (vm.NewFirstName, vm.NewLastName, vm.NewEmail) = ("Bea", "Lim", "bea@demo.io");
        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.True(vm.IsRegistering);
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
