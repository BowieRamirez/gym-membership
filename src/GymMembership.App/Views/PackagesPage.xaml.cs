using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class PackagesPage : BasePage
{
	public PackagesPage(PackagesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
