using GymMembership.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GymMembership.App;

public partial class App : Application
{
	readonly IServiceProvider _services;

	public App(IServiceProvider services)
	{
		InitializeComponent();
		_services = services;
		// D14: "are you sure?" uses the native popup on whatever page is showing
		Core.Services.Confirm.Popup = (title, message, action) =>
			Windows.FirstOrDefault()?.Page is { } page ? page.DisplayAlertAsync(title, message, action, "Go back") : Task.FromResult(false);
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(_services.GetRequiredService<LoginPage>()) { Title = "Gym Center" };
		if (DeviceInfo.Idiom == DeviceIdiom.Desktop) { window.Width = 1180; window.Height = 780; }
		if (DeviceInfo.Platform == DevicePlatform.WinUI)
		{
			// the side menu's backdrop runs up under the title bar, so the bar takes the same colour (else Windows paints it blue in light mode)
			var bar = new TitleBar { Title = "Gym Center" };
			bar.SetAppThemeColor(VisualElement.BackgroundColorProperty, (Color)Resources["BackdropLight"], (Color)Resources["BackdropDark"]);
			bar.SetAppThemeColor(TitleBar.ForegroundColorProperty, (Color)Resources["InkLight"], (Color)Resources["InkDark"]);
			window.TitleBar = bar;
		}
		return window;
	}
}
