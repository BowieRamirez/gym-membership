using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

/// <summary>The bar at the top of every signed-in screen: your picture, the page title, the alerts bell and Sign out.</summary>
public partial class HeaderBar : ContentView
{
	public HeaderBar() => InitializeComponent();

	/// <summary>Called once by each shell. Follows the shell's current page for the title and refreshes the badge after every navigation.</summary>
	public void Attach(Shell shell, IServiceProvider sp)
	{
		var vm = sp.GetRequiredService<HeaderViewModel>();
		BindingContext = vm;
		void Update() { TitleLabel.Text = shell.CurrentPage?.Title; _ = vm.RefreshAsync(); }
		shell.Navigated += (_, _) => Update();
		shell.Loaded += (_, _) => Update();
	}

	static void GoTo(string route) => MainThread.BeginInvokeOnMainThread(async () => await Shell.Current.GoToAsync(route));
	void OnAvatarTapped(object? sender, TappedEventArgs e) => GoTo("//settings");
	void OnBellClicked(object? sender, EventArgs e) => GoTo("//alerts");
}
