using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class SettingsPictureTests
{
    sealed class Picker((string, byte[])? pick) : IProofPicker
    {
        public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult(pick is { } p ? ((string, byte[])?)p : null);
    }

    sealed class Nav : IAppNavigator
    {
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() { }
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToMembership() { }
        public void ApplyTheme(string theme) { }
    }

    static async Task<(SettingsViewModel Vm, ProfileService Profile)> Build((string, byte[])? pick)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var profile = new ProfileService(gw);
        return (new SettingsViewModel(new SettingsService(gw), new AuthService(gw), new Nav(), profile, new Picker(pick)), profile);
    }

    [Fact]
    public async Task Opening_settings_shows_the_initials()
    {
        var (vm, _) = await Build(null);
        await vm.OnAppearingAsync();
        Assert.Equal("AT", vm.Letters);
    }

    [Fact]
    public async Task Choosing_a_picture_saves_it_and_says_so()
    {
        var (vm, profile) = await Build(("me.png", [1, 2, 3]));
        await vm.ChangePhotoCommand.ExecuteAsync(null);

        Assert.NotNull((await profile.MeAsync()).Value!.AvatarPath);
        Assert.Equal("Picture updated.", vm.Notice);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var (vm, profile) = await Build(null);
        await vm.ChangePhotoCommand.ExecuteAsync(null);

        Assert.Null((await profile.MeAsync()).Value!.AvatarPath);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_shows_plain_words()
    {
        var (vm, profile) = await Build(("cv.pdf", [1]));
        await vm.ChangePhotoCommand.ExecuteAsync(null);

        Assert.Equal("Choose a JPG, PNG or WebP picture under 5 MB.", vm.ErrorMessage);
        Assert.Null((await profile.MeAsync()).Value!.AvatarPath);
    }
}
