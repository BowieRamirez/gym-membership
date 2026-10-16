using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using GymMembership.App.Views;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>The bar at the top of every signed-in screen: your picture, the page title, the alerts bell and Sign out.</summary>
public partial class HeaderBar : ContentView
{
	// The picture opens Settings as a page on top of whatever you are on, with a back arrow. Switching shell items instead would
	// leave the front desk's bottom bar behind with no way back to it. The bell opens the alerts panel (below).
	static HeaderBar() => Routing.RegisterRoute("header-settings", typeof(SettingsPage));

	IServiceProvider? _sp;
	bool _panelOpen;

	public HeaderBar() => InitializeComponent();

	/// <summary>Called once by each shell. Follows the shell's current page for the title and refreshes the badge after every navigation.</summary>
	public void Attach(Shell shell, IServiceProvider sp)
	{
		_sp = sp;
		var vm = sp.GetRequiredService<HeaderViewModel>();
		BindingContext = vm;
		void Update() { TitleLabel.Text = shell.CurrentPage?.Title; _ = vm.RefreshAsync(); }
		shell.Navigated += (_, _) => Update();
		shell.Loaded += (_, _) => Update();
		shell.Unloaded += (_, _) => vm.Dispose();   // a rebuilt shell gets a new header; this one must stop listening

		// look again once a minute while the app is open; every fifth look also tells the server you are here (last active)
		var profile = sp.GetRequiredService<IProfileService>();
		var ticks = 0;
		var timer = Application.Current!.Dispatcher.CreateTimer();
		timer.Interval = TimeSpan.FromSeconds(60);
		timer.Tick += (_, _) => { _ = vm.RefreshAsync(); if (++ticks % 5 == 0) _ = profile.TouchSeenAsync(); };
		timer.Start();
		shell.Loaded += (_, _) => _ = profile.TouchSeenAsync();   // and once when the app opens
		shell.Unloaded += (_, _) => timer.Stop();
#if WINDOWS
		shell.FlyoutFooter = new SignOutFooter { BindingContext = vm };   // Sign out sits at the bottom of the side menu on Windows
		shell.Loaded += (_, _) => HideOverflowButton(shell);
		shell.Navigated += (_, _) => HideOverflowButton(shell);
#endif
	}

#if WINDOWS
	// Windows draws a "…" overflow button next to the title bar that has nothing in it. Collapse it on whatever command bar the shell shows.
	static void HideOverflowButton(Shell shell)
	{
		static void Walk(Microsoft.UI.Xaml.DependencyObject node)
		{
			if (node is Microsoft.UI.Xaml.Controls.CommandBar bar) bar.OverflowButtonVisibility = Microsoft.UI.Xaml.Controls.CommandBarOverflowButtonVisibility.Collapsed;
			for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node); i++) Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i));
		}
		if (shell.Handler?.PlatformView is Microsoft.UI.Xaml.DependencyObject root) Walk(root);
	}
#endif

	// already looking at it: do nothing, so repeated taps do not stack copies
	static void Open<TPage>(string route) where TPage : Page => MainThread.BeginInvokeOnMainThread(async () =>
	{
		if (Shell.Current.CurrentPage is not TPage) await Shell.Current.GoToAsync(route);
	});
	// the same flat, hairline-edged box as the app's other panels, in the colors of the theme that is showing
	static RoundRectangle HairlineBox()
	{
		var key = Application.Current!.RequestedTheme == AppTheme.Dark ? "LineDark" : "LineLight";
		var line = Application.Current.Resources.TryGetValue(key, out var v) && v is Color c ? c : Colors.Gray;
		return new RoundRectangle { CornerRadius = 6, StrokeThickness = 1, Stroke = line };
	}

	void OnAvatarTapped(object? sender, TappedEventArgs e) => Open<SettingsPage>("header-settings");

	async void OnBellClicked(object? sender, EventArgs e)
	{
		if (_panelOpen || _sp is null || Shell.Current is not { } shell) return;   // a second tap while it is open (or opening) does nothing
		_panelOpen = true;
		NotificationsViewModel? vm = null;
		try
		{
			vm = _sp.GetRequiredService<NotificationsViewModel>();
			var panel = new NotificationPanel(vm);
			_ = vm.OnAppearingAsync();   // load while it opens
			await shell.ShowPopupAsync(panel, new PopupOptions { Shadow = null, Shape = HairlineBox() });   // returns when it closes (outside tap or Close)
		}
		catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }   // an async void handler: an escaped error here would end the app, and a bell that does nothing is better
		finally { vm?.Dispose(); _panelOpen = false; }
	}
}
