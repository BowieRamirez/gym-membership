using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class ShiftsPage : BasePage
{
	public ShiftsPage(ShiftsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
