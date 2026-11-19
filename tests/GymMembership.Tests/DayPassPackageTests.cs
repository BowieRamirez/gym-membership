using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>Owner fixes 2026-11-19 Q1 = (b), Q9 (0098): the Day pass is a package; its term ends at midnight in Manila.</summary>
public class DayPassPackageTests
{
    static async Task<(DemoGateway Gw, MembershipService Member, MembershipPackage DayPass)> NewMember()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw);
        return (gw, member, (await member.ListPackagesAsync()).Value!.Single(p => p.IsDayPass));
    }

    [Fact]
    public async Task A_day_pass_bought_online_lasts_until_midnight_in_Manila()
    {
        var (gw, member, dayPass) = await NewMember();
        var pay = (await member.AvailAsync(dayPass.Id)).Value!;
        Assert.True((await member.PayOnlineAsync(pay.Id)).Value!.Paid);
        var term = (await gw.ListAsync<MembershipTerm>(t => t.PaymentId == pay.Id)).Single();
        Assert.Equal(term.StartsAt.AddHours(8).Date.AddDays(1).AddHours(-8), term.EndsAt);   // next midnight, Manila (UTC+8)
        Assert.True((await member.AccessAsync()).Value!.Active);
    }

    [Fact]
    public async Task Someone_with_a_membership_cannot_buy_a_day_pass()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);   // Alex, active
        var member = new MembershipService(gw);
        var dayPass = (await member.ListPackagesAsync()).Value!.Single(p => p.IsDayPass);
        Assert.Equal("already_availed", (await member.AvailAsync(dayPass.Id)).Message);
    }

    [Fact]
    public async Task A_day_pass_does_not_book_another_day()
    {
        var (gw, member, dayPass) = await NewMember();
        await member.PayOnlineAsync((await member.AvailAsync(dayPass.Id)).Value!.Id);
        var pool = (await gw.ListAsync<Amenity>(a => a.Name == "Pool")).Single();
        var start = DateTime.UtcNow.AddHours(8).Date.AddDays(2).AddHours(10 - 8);   // 10 am Manila, two days on
        var booked = await new ReservationService(gw).BookAsync(pool.Id, start, 60, PayMethod.Qr);
        Assert.Equal("outside_membership", booked.Message);
    }

    [Fact]
    public void A_day_pass_reads_today_only_and_a_package_its_days()
    {
        Assert.Equal("Today only, until midnight", new MembershipPackage { DurationDays = 1, IsDayPass = true }.LengthText);
        Assert.Equal("30 days of access", new MembershipPackage { DurationDays = 30 }.LengthText);
    }

    [Fact]   // a member with a membership can't buy it, so Packages doesn't offer it
    public async Task Packages_hides_the_day_pass_from_a_member()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);   // Alex, active
        var vm = new GymMembership.Core.ViewModels.PackagesViewModel(new MembershipService(gw), new ConfirmationTests.Nav(), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        Assert.NotEmpty(vm.Packages);
        Assert.DoesNotContain(vm.Packages, p => p.IsDayPass);
    }

    [Fact]
    public void Outside_the_membership_has_its_own_sentence() =>
        Assert.Contains("membership", ErrorText.For(AppErrorKind.Validation, "outside_membership"));

    [Fact]   // final review (0105): the flag travels with a new version and can't change once on sale
    public async Task A_new_version_of_the_day_pass_is_a_day_pass_and_the_flag_is_locked_on_sale()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var dayPass = (await gw.ListAsync<MembershipPackage>()).Single(p => p.IsDayPass);
        var draft = (await admin.NewPackageVersionAsync(dayPass.Id)).Value;
        Assert.True((await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == draft).IsDayPass);
        var monthly = (await gw.ListAsync<MembershipPackage>()).Single(p => p.Name == "Monthly" && p.PublishedAt is not null);
        monthly.IsDayPass = true;
        Assert.Equal("package_published", (await admin.SavePackageAsync(monthly)).Message);
    }
}
