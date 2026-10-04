using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RevenuePage : BasePage
{
	public RevenuePage(RevenueViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
