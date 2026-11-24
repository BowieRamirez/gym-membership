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

    [Fact]   // owner 2026-11-24: Packages shows memberships and the Day pass apart, on the same page
    public async Task Packages_shows_memberships_and_day_passes_apart()
    {
        var (gw, _, _) = await NewMember();
        var vm = new GymMembership.Core.ViewModels.PackagesViewModel(new MembershipService(gw), new ConfirmationTests.Nav(), new SubscriptionService(gw));
        await vm.OnAppearingAsync();
        Assert.NotEmpty(vm.Memberships);
        Assert.DoesNotContain(vm.Memberships, p => p.IsDayPass);
        Assert.Equal(vm.Memberships.OrderBy(p => p.DurationDays).ThenBy(p => p.Price).Select(p => p.Id), vm.Memberships.Select(p => p.Id));
        Assert.All(vm.DayPasses, p => Assert.True(p.IsDayPass));
        Assert.True(vm.HasDayPasses);
        Assert.Equal("Buy day pass", vm.DayPasses[0].BuyText);
        Assert.Equal("Choose next term", vm.Memberships[0].BuyText);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);   // Alex has a membership: no Day pass section
        await vm.OnAppearingAsync();
        Assert.Empty(vm.DayPasses);
        Assert.False(vm.HasDayPasses);
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

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]   // 0106: a Day pass booking must end by midnight; a Monthly member keeps the start-only rule
    public async Task A_day_pass_booking_cannot_run_past_midnight_but_monthly_can()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero));   // 12:00 Manila
        var gw = new DemoGateway(clock: clock);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var settings = (await gw.ListAsync<GymSettings>()).Single();
        settings.OpensAt = TimeSpan.Zero; settings.ClosesAt = new TimeSpan(23, 30, 0);
        await gw.UpdateAsync(settings);

        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var member = new MembershipService(gw);
        var dayPass = (await member.ListPackagesAsync()).Value!.Single(p => p.IsDayPass);
        var pay = (await member.AvailAsync(dayPass.Id)).Value!;
        await member.PayOnlineAsync(pay.Id);
        var pool = (await gw.ListAsync<Amenity>(a => a.Name == "Pool")).Single();
        DateTime Manila(int hour) => new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc).AddHours(hour - 8);

        var res = new ReservationService(gw);
        Assert.Equal("outside_membership", (await res.BookAsync(pool.Id, Manila(23), 120, PayMethod.Qr)).Message);
        Assert.True((await res.BookAsync(pool.Id, Manila(21), 60, PayMethod.Qr)).Ok);

        // Monthly member: advance to the last day of Alex's term; booking that starts before EndsAt and runs past it still works
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var alexTerm = (await gw.ListAsync<MembershipTerm>(t => t.MemberId == gw.CurrentUserId && t.CancelledAt == null)).OrderByDescending(t => t.EndsAt).First();
        var start = new DateTime(alexTerm.EndsAt.Year, alexTerm.EndsAt.Month, alexTerm.EndsAt.Day, alexTerm.EndsAt.Hour, 0, 0, DateTimeKind.Utc).AddHours(-1);
        clock.Now = new DateTimeOffset(start.AddHours(-3), TimeSpan.Zero);
        Assert.True((await res.BookAsync(pool.Id, start, 120, PayMethod.Qr)).Ok);
    }

    [Fact]
    public async Task Gym_settings_hides_walk_in_price_while_day_pass_package_is_on_sale()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var vm = new GymMembership.Core.ViewModels.GymSettingsViewModel(admin);
        await vm.OnAppearingAsync();
        Assert.False(vm.CanEditWalkInPrice);
        Assert.True(vm.HasDayPassPackage);
        Assert.Contains("Day", vm.DayPassPackageNote);

        await gw.RpcAsync("archive_catalog_record", new() { ["p_type"] = "plan", ["p_id"] = vm.DayPassPackage!.Id, ["p_reason"] = "Retiring" });
        await vm.OnAppearingAsync();
        Assert.True(vm.CanEditWalkInPrice);
        Assert.False(vm.HasDayPassPackage);
    }

    [Fact]
    public async Task Gym_settings_cannot_be_saved_before_they_have_loaded()
    {
        // UI test 2026-11-23: on a slow line the form showed defaults for seconds; Save now waits for the real values
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new GymMembership.Core.ViewModels.GymSettingsViewModel(new AdminService(gw));
        Assert.False(vm.SettingsLoaded);
        Assert.False(vm.SaveCommand.CanExecute(null));
        await vm.OnAppearingAsync();
        Assert.True(vm.SettingsLoaded);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Gym_settings_shows_the_plain_day_pass_price_not_the_coach_included_one()
    {
        // UI test 2026-11-23: showed the Daypass (With coach included) price while walk-ins pay the plain Day pass
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new GymMembership.Core.ViewModels.GymSettingsViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        await gw.RpcAsync("archive_catalog_record", new() { ["p_type"] = "plan", ["p_id"] = vm.DayPassPackage!.Id, ["p_reason"] = "Reorder" });
        // the coach-included one comes first in the list, as on the live project
        var coached = await gw.InsertAsync(new MembershipPackage { Name = "Day pass + coach", Description = "x", Price = 999, DurationDays = 1, IsDayPass = true, IncludesCoach = true, IsActive = true });
        await gw.RpcAsync("publish_catalog_item", new() { ["p_kind"] = "plan", ["p_id"] = coached.Id });
        var plain = await gw.InsertAsync(new MembershipPackage { Name = "Day pass", Description = "x", Price = 60, DurationDays = 1, IsDayPass = true, IsActive = true });
        await gw.RpcAsync("publish_catalog_item", new() { ["p_kind"] = "plan", ["p_id"] = plain.Id });
        await vm.OnAppearingAsync();
        Assert.Equal(plain.Id, vm.DayPassPackage!.Id);
    }
}
