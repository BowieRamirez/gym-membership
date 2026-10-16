using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class HomePage : BasePage
{
	public HomePage(HomeViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// the numbers settle on their own while the desk is open
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);

	void OnTile(object? sender, TappedEventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is HomeTile tile) (Shell.Current as StaffShell)?.ShowDeskPage(tile.Route);
	}
}
