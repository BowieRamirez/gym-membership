using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;
using Microsoft.Maui.Platform;

namespace GymMembership.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		StyleSystemBars();
		if (Microsoft.Maui.Controls.Application.Current is { } app) app.RequestedThemeChanged += (_, _) => StyleSystemBars();
	}

	// the status bar follows the app's theme (the user's saved one, not only the phone's): dark icons in light mode. From Android 15 the
	// page or header shows behind it (PhoneNavigation paints the header's app bar); older versions take the colour below.
	void StyleSystemBars()
	{
		if (Window is not { } window || Microsoft.Maui.Controls.Application.Current is not { } app) return;
		var dark = app.RequestedTheme == AppTheme.Dark;
#pragma warning disable CA1422 // ignored from Android 15
		window.SetStatusBarColor((app.Resources[dark ? "BackdropDark" : "BackdropLight"] as Color ?? Colors.Black).ToPlatform());
#pragma warning restore CA1422
		var bars = WindowCompat.GetInsetsController(window, window.DecorView);
		bars.AppearanceLightStatusBars = !dark;
		bars.AppearanceLightNavigationBars = !dark;
	}
}
