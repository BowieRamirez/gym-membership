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
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(_services.GetRequiredService<LoginPage>()) { Title = "Gym Membership" };
		if (DeviceInfo.Idiom == DeviceIdiom.Desktop) { window.Width = 1180; window.Height = 780; }
		return window;
	}
}
