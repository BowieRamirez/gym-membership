using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class HeaderTests
{
    sealed class Nav : IAppNavigator
    {
        public bool AtLogin;
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() => AtLogin = true;
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToMembership() { }
        public void ApplyTheme(string theme) { }
    }

    static async Task<(DemoGateway Gw, HeaderViewModel Vm, Nav Nav)> Build(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var nav = new Nav();
        return (gw, new HeaderViewModel(new ProfileService(gw), new NotificationService(gw), new AuthService(gw), nav), nav);
    }

    [Fact]
    public async Task Refresh_shows_the_initials_and_the_unread_count()
    {
        var (gw, vm, _) = await Build("member@demo.io");
        await vm.RefreshAsync();

        var unread = (await new NotificationService(gw).ListAsync()).Value!.Count(n => n.ReadAt is null);
        Assert.Equal("AT", vm.Letters);
        Assert.Null(vm.AvatarUrl);
        Assert.True(unread > 0);
        Assert.Equal(unread, vm.UnreadCount);
        Assert.True(vm.HasUnread);
    }

    [Fact]
    public async Task Reading_a_notification_lowers_the_badge()
    {
        var (gw, vm, _) = await Build("member@demo.io");
        var notes = new NotificationService(gw);
        await vm.RefreshAsync();
        var before = vm.UnreadCount;

        await notes.MarkReadAsync((await notes.ListAsync()).Value!.First(n => n.ReadAt is null).Id);
        await vm.RefreshAsync();

        Assert.Equal(before - 1, vm.UnreadCount);
    }

    [Theory]
    [InlineData(0, false, "0", "Alerts")]
    [InlineData(1, true, "1", "Alerts, 1 unread")]
    [InlineData(99, true, "99", "Alerts, 99 unread")]
    [InlineData(100, true, "99+", "Alerts, 100 unread")]
    public async Task The_badge_hides_at_zero_caps_at_99_and_the_bell_says_the_count(int count, bool visible, string text, string label)
    {
        var (_, vm, _) = await Build("member@demo.io");
        vm.UnreadCount = count;
        Assert.Equal(visible, vm.HasUnread);
        Assert.Equal(text, vm.UnreadText);
        Assert.Equal(label, vm.AlertsLabel);
    }

    [Fact]
    public async Task If_the_server_cannot_be_reached_the_bar_stays_quiet()
    {
        var (gw, vm, _) = await Build("member@demo.io");
        gw.FailWith = new HttpRequestException("offline");
        await vm.RefreshAsync();   // must not throw

        Assert.Null(vm.ErrorMessage);   // a banner under the title bar would be noise
        Assert.Equal("?", vm.Letters);
    }

    [Fact]
    public async Task A_new_picture_refreshes_the_bar()
    {
        var (gw, _, _) = await Build("member@demo.io");
        var profile = new ProfileService(gw);
        var vm2 = new HeaderViewModel(profile, new NotificationService(gw), new AuthService(gw), new Nav());
        Assert.Equal("?", vm2.Letters);

        await profile.SetAvatarAsync("a.jpg", [1]);   // raises AvatarChanged, which makes the bar reload
        await Task.Delay(100);

        Assert.Equal("AT", vm2.Letters);
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_goes_to_the_login_page()
    {
        var (gw, vm, nav) = await Build("member@demo.io");
        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.Null(gw.CurrentUserId);
        Assert.True(nav.AtLogin);
    }
}
