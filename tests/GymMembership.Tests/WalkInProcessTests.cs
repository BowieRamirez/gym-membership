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
