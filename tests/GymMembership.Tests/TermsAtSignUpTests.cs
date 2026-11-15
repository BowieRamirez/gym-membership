using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 11, G24/G25 (owner, 2026-11-15; 0069): signing up needs the Terms and Policy box ticked; the version agreed goes to the server.
public class TermsAtSignUpTests
{
    [Fact]
    public async Task Creating_an_account_without_the_tick_says_so_and_creates_nothing()
    {
        var gw = new DemoGateway();
        var vm = new RegisterViewModel(new AuthService(gw), new NamesAndEmailTests.NullNavigator())
            { FirstName = "Tess", LastName = "Reyes", Email = "tess@demo.io", Password = "secret12", ConfirmPassword = "secret12" };
        Assert.False(vm.AgreedToTerms);
        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.Equal("Tick the box to agree to the Terms and Policy.", vm.TermsError);   // under the box, not in the banner
        Assert.Null(vm.ErrorMessage);
        Assert.Null(gw.CurrentUserId);

        vm.AgreedToTerms = true;
        Assert.Null(vm.TermsError);   // ticking clears it at once
        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.NotNull(gw.CurrentUserId);
    }

    [Fact]
    public void Reading_the_terms_never_ticks_the_box_or_loses_what_was_typed()
    {
        var vm = new RegisterViewModel(new AuthService(new DemoGateway()), new NamesAndEmailTests.NullNavigator()) { FirstName = "Tess" };
        vm.OpenTermsCommand.Execute(null);
        Assert.True(vm.TermsOpen);
        vm.CloseTermsCommand.Execute(null);
        Assert.False(vm.TermsOpen);
        Assert.False(vm.AgreedToTerms);
        Assert.Equal("Tess", vm.FirstName);
    }

    [Fact]
    public async Task The_server_rule_is_mirrored_in_the_demo()
    {
        Assert.Equal("terms_required", (await new AuthService(new DemoGateway()).RegisterAsync("Tess", "Reyes", "tess@demo.io", "secret12", false)).Message);
        await Assert.ThrowsAnyAsync<Exception>(() => new DemoGateway().SignUpAsync("tess@demo.io", "secret12", "Tess", "Reyes", " "));
    }

    [Fact]
    public void The_terms_shipped_with_the_app_carry_the_version_and_the_refund_rule()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "gym-membership.slnx"))) root = Path.GetDirectoryName(root)!;
        var text = File.ReadAllText(Path.Combine(root, "src", "GymMembership.App", "Resources", "Raw", "terms.md"));
        Assert.Contains($"Version {TermsInfo.Version}", text.Split('\n').Take(4).Aggregate((a, b) => a + b));
        Assert.Contains("first 7 days", text);
        Assert.Equal("2026-11-15", TermsInfo.Version);
    }
}
