using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// G23 follow-up (owner, 2026-11-15): the refund rule is said before a membership is paid for, by the member and at the desk.
public class RefundLineTests
{
    static List<string> Answer()
    {
        var asked = new List<string>();
        Confirm.Answer = (title, message, _) => { asked.Add($"{title}|{message}"); return Task.FromResult(false); };
        return asked;
    }

    [Fact]
    public void The_packages_page_says_the_same_sentence()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "gym-membership.slnx"))) root = Path.GetDirectoryName(root)!;
        var xaml = File.ReadAllText(Path.Combine(root, "src", "GymMembership.App", "Views", "PackagesPage.xaml"));
        Assert.Contains(System.Net.WebUtility.HtmlEncode(RefundRules.Line).Replace("&#39;", "'"), xaml);
    }

    [Theory]
    [InlineData("Membership", true)]
    [InlineData("Renewal", true)]
    [InlineData("Day pass", false)]
    public async Task The_desk_is_told_to_say_it_for_a_membership_but_not_a_day_pass(string kind, bool said)
    {
        var asked = Answer();
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        var vm = new CashierViewModel(new PaymentStaffService(gw), new AttendanceService(gw));
        await vm.OnAppearingAsync();
        vm.Kind = kind;
        if (kind == "Day pass") vm.WalkInName = "Pedro Ramos";
        else
        {
            vm.Search = kind == "Renewal" ? "Alex" : "Carlo"; await vm.SuggestNowAsync(); vm.Member = vm.Hits.First();
            await Task.Delay(50);
            vm.PickItemCommand.Execute(vm.Options[0]);
        }
        await vm.RecordCommand.ExecuteAsync(null);
        Assert.Equal(said, Assert.Single(asked).Contains("Tell them: " + RefundRules.Line));
    }
}
