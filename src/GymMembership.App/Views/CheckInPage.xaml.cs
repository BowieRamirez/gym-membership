using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CheckInPage : BasePage
{
	public CheckInPage(CheckInViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
