using GymMembership.App.Views;
using GymMembership.Core.Services;

namespace GymMembership.App.Services;

public sealed class AppNavigator(IServiceProvider sp) : IAppNavigator
{
    public void GoTo(AppRole role)
    {
        if (role == AppRole.Member) { _ = ShowMemberShellAsync(); return; }
        Show(role == AppRole.Coach ? new CoachShell() : new StaffShell(role));
        _ = ApplyStoredThemeAsync();
    }

    // a member's menu depends on whether they have an active plan, so ask before building it
    async Task ShowMemberShellAsync()
    {
        var access = await sp.GetRequiredService<IMembershipService>().AccessAsync();
        Show(new MemberShell(sp, access.Value ?? new Access(false, false)));
        _ = ApplyStoredThemeAsync();
    }

    public void GoToRegister() => Show(sp.GetRequiredService<RegisterPage>());

    public void GoToLogin()
    {
        ApplyTheme("system");
        Show(sp.GetRequiredService<LoginPage>());
    }

    public void GoToMessages() => MainThread.BeginInvokeOnMainThread(async () => await Shell.Current.GoToAsync("//messages"));

    public void ApplyTheme(string theme) => MainThread.BeginInvokeOnMainThread(() =>
        Application.Current!.UserAppTheme = theme switch { "dark" => AppTheme.Dark, "light" => AppTheme.Light, _ => AppTheme.Unspecified });

    static void Show(Page page) => MainThread.BeginInvokeOnMainThread(() => Application.Current!.Windows[0].Page = page);

    async Task ApplyStoredThemeAsync()
    {
        var settings = await sp.GetRequiredService<ISettingsService>().LoadAsync();
        if (settings.Ok) ApplyTheme(settings.Value!.Theme);
    }
}

public sealed class MauiProofPicker : IProofPicker
{
    public async Task<(string FileName, byte[] Data)?> PickAsync()
    {
        try
        {
            var file = await MediaPicker.Default.PickPhotoAsync();
            if (file is null) return null;
            await using var stream = await file.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            return (file.FileName, ms.ToArray());
        }
        catch (Exception) { return null; } // cancelled or no picker available: treat as "no proof attached"
    }
}
