using GymMembership.App.Views;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

/// <summary>The bar at the top of every signed-in screen: your picture, the page title, the alerts bell and Sign out.</summary>
public partial class HeaderBar : ContentView
{
	// The bell and the picture open Alerts and Settings as pages on top of whatever you are on, with a back arrow. Switching
	// shell items instead would leave the front desk's bottom bar behind with no way back to it.
	static HeaderBar()
	{
		Routing.RegisterRoute("header-alerts", typeof(NotificationsPage));
		Routing.RegisterRoute("header-settings", typeof(SettingsPage));
	}

	public HeaderBar() => InitializeComponent();

	/// <summary>Called once by each shell. Follows the shell's current page for the title and refreshes the badge after every navigation.</summary>
	public void Attach(Shell shell, IServiceProvider sp)
	{
		var vm = sp.GetRequiredService<HeaderViewModel>();
		BindingContext = vm;
		void Update() { TitleLabel.Text = shell.CurrentPage?.Title; _ = vm.RefreshAsync(); }
		shell.Navigated += (_, _) => Update();
		shell.Loaded += (_, _) => Update();
		shell.Unloaded += (_, _) => vm.Dispose();   // a rebuilt shell gets a new header; this one must stop listening
	}

	// already looking at it: do nothing, so repeated taps do not stack copies
	static void Open<TPage>(string route) where TPage : Page => MainThread.BeginInvokeOnMainThread(async () =>
	{
		if (Shell.Current.CurrentPage is not TPage) await Shell.Current.GoToAsync(route);
	});
	void OnAvatarTapped(object? sender, TappedEventArgs e) => Open<SettingsPage>("header-settings");
	void OnBellClicked(object? sender, EventArgs e) => Open<NotificationsPage>("header-alerts");
}
