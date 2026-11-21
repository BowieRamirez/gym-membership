using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CoachSessionBookingTests
{
    sealed class StubNav : IAppNavigator
    {
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() { }
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToRequests() { }
        public void GoToMembership() { }
        public void GoToSessions() { }
        public void ApplyTheme(string theme) { }
    }

    [Fact]
    public async Task SessionsViewModel_book_coach_shows_package_price_and_keeps_day_time_on_coach_taken()
    {
        var gw = new DemoGateway();
        // Add a future shift for u-coach1 +2 days 10am-2pm Manila (inside Alex's 5-day active term)
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8).AddDays(2));
        var manila = TimeSpan.FromHours(8);
        var sStart = new DateTimeOffset(tomorrow.ToDateTime(TimeOnly.MinValue).AddHours(10), manila).UtcDateTime;
        var sEnd = new DateTimeOffset(tomorrow.ToDateTime(TimeOnly.MinValue).AddHours(14), manila).UtcDateTime;
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        await gw.InsertAsync(new ShiftPost { CoachId = "u-coach1", Start = sStart, End = sEnd, Status = Status.Scheduled });

        // Alex is on Monthly (CoachAddonPrice = 250)
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var sesSvc = new SessionService(gw);
        var memSvc = new MembershipService(gw);
        var vm = new SessionsViewModel(sesSvc, memSvc);
        await vm.OnAppearingAsync();
        Assert.Contains("₱250", vm.PackageCoachText);

        var direct = await sesSvc.FreeCoachesAsync(tomorrow);
        Assert.True(direct.Ok, direct.Message ?? direct.Error.ToString());
        Assert.NotEmpty(direct.Value!);

        await vm.OpenBookCoachCommand.ExecuteAsync(null);
        vm.BookDay = tomorrow.ToDateTime(TimeOnly.MinValue);
        await vm.LoadFreeCoachesCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.FreeCoaches);
        var coach = vm.FreeCoaches.First(c => c.CoachId == "u-coach1");
        vm.SelectedFreeCoach = coach;
        var hr = vm.AvailableHours.First(h => h.Hour == 10);
        vm.BookHour = hr;

        // Someone else (Bea) books 10am-11am right before Alex confirms (both buy coach access first)
        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var beaPay = (await gw.ListAsync<Payment>(p => p.MemberId == "u-bea" && p.Status == Status.Pending)).First();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await new PaymentStaffService(gw).VerifyAsync(beaPay.Id, true, null);
        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var beaCa = await memSvc.BuyCoachAccessAsync();
        await memSvc.PayOnlineAsync(beaCa.Value!.Id);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await sesSvc.DeskBookCoachAsync("u-bea", "u-coach1", sStart, 1, "cash", null);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        foreach (var s in (await gw.ListAsync<TrainingSession>(x => x.MemberId == "u-alex" && x.Status == Status.Scheduled)))
            await sesSvc.CancelSessionAsync(s.Id, "Reset for test");
        var alexCa = await memSvc.BuyCoachAccessAsync();
        await memSvc.PayOnlineAsync(alexCa.Value!.Id);

        await vm.BookCoachCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("booked", vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        // Keeps the chosen day!
        Assert.Equal(tomorrow.ToDateTime(TimeOnly.MinValue).Date, vm.BookDay.Date);
    }

    [Fact]
    public async Task PackagesViewModel_day_pass_hides_add_a_coach_when_no_free_hour_today()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new PackagesViewModel(new MembershipService(gw), new StubNav(), new SubscriptionService(gw), null, new SessionService(gw));
        await vm.OnAppearingAsync();
        var dayPass = (await gw.ListAsync<MembershipPackage>(p => p.IsDayPass)).First();
        // No shifts seeded for today -> Add a coach stays hidden
        vm.OpenBuyCommand.Execute(dayPass);
        await Task.Delay(50);
        Assert.False(vm.HasDayPassCoach);
    }
}
