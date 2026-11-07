using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class PackagesPage : BasePage
{
	public PackagesPage(PackagesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnAvail(object? sender, EventArgs e) => Row<PackagesViewModel>(sender, v => v.AvailCommand);
	void OnToggleRules(object? sender, EventArgs e) => Rules.IsVisible = !Rules.IsVisible;   // phones: the rules stay folded until asked for
}
