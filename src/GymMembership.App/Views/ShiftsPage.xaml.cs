using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class ShiftsPage : BasePage
{
	// G30 (owner, 2026-11-15): the coach's members moved from here to the Sessions page
	public ShiftsPage(ShiftsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
