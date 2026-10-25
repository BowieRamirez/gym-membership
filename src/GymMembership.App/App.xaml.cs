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
		var window = new Window(_services.GetRequiredService<LoginPage>()) { Title = "Gym Membership" };
		if (DeviceInfo.Idiom == DeviceIdiom.Desktop) { window.Width = 1180; window.Height = 780; }
		return window;
	}
}
