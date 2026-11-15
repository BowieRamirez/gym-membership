using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Item 3 / G5 (2026-11-12, 0059): accounts with a first name and no last name. Nothing is guessed: staff see them under
// "Name incomplete" and correct them; the person adds the last name once in Settings. The demo's Joy has a first name only.
public class CompleteNamesTests
{
    sealed class Nav : IAppNavigator
    {
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() { }
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToRequests() { }
        public void GoToMembership() { }
        public void ApplyTheme(string theme) { }
    }
    sealed class NoPicker : IProofPicker { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string, byte[])?>(null); }

    static async Task<(DemoGateway Gw, SettingsViewModel Vm)> Settings(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new SettingsViewModel(new SettingsService(gw), new AuthService(gw), new Nav(), new ProfileService(gw), new NoPicker());
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    [Fact]
    public async Task Users_and_roles_count_and_filter_the_accounts_whose_name_is_incomplete()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new UsersViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.IncompleteCount);
        Assert.Single(vm.Users, u => u.NameIncomplete);

        vm.OnlyIncomplete = true;
        Assert.Equal(["Joy"], vm.Users.Select(u => u.Username));
        vm.OnlyIncomplete = false;
        Assert.True(vm.Users.Count > 1);
    }

    [Fact]
    public async Task A_system_admin_correction_completes_the_name_and_the_count_drops()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new UsersViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        vm.StartCorrectNameCommand.Execute(vm.Users.Single(u => u.NameIncomplete));
        (vm.CorrectLastName, vm.CorrectReason) = ("Reyes", "Missing last name");
        await vm.ConfirmCorrectNameCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(0, vm.IncompleteCount);
    }

    [Fact]
    public async Task The_member_adds_their_last_name_once_in_Settings()
    {
        var (gw, vm) = await Settings("joy@demo.io");
        Assert.True(vm.NeedsLastName);
        vm.MissingLastName = " Reyes ";
        await vm.AddLastNameCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(("Reyes", "Joy Reyes", false), (vm.LastName, vm.SavedName, vm.NeedsLastName));

        // the server refuses a second change: names are locked again (0040)
        Assert.Equal("name_complete", (await new ProfileService(gw).AddMyLastNameAsync("Other")).Message);
    }

    [Fact]
    public async Task Adding_a_last_name_tells_the_open_header_to_redraw_its_initials()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("joy@demo.io", DemoGateway.DemoPassword);
        var profile = new ProfileService(gw);
        var told = 0;
        profile.AvatarChanged += () => told++;
        Assert.True((await profile.AddMyLastNameAsync("Reyes")).Ok);
        Assert.Equal(1, told);
        Assert.False((await profile.AddMyLastNameAsync("Again")).Ok);
        Assert.Equal(1, told);   // a refused change redraws nothing
    }

    [Fact]
    public async Task A_complete_name_offers_nothing_and_a_bad_last_name_is_refused_in_plain_words()
    {
        var (gw, vm) = await Settings("member@demo.io");
        Assert.False(vm.NeedsLastName);
        Assert.Equal("name_complete", (await new ProfileService(gw).AddMyLastNameAsync("Santos")).Message);

        var (_, joy) = await Settings("joy@demo.io");
        joy.MissingLastName = "R2D2";
        await joy.AddLastNameCommand.ExecuteAsync(null);
        Assert.Equal(ErrorText.For(AppErrorKind.Validation, "bad_name"), joy.ErrorMessage);
        Assert.True(joy.NeedsLastName);
        Assert.NotEqual("name_complete", ErrorText.For(AppErrorKind.Validation, "name_complete"));
    }
}
