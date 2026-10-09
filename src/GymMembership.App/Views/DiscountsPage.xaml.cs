using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class DiscountsPage : BasePage
{
	public DiscountsPage(DiscountsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
