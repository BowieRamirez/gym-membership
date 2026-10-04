using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class LoginPage : BasePage
{
	public LoginPage(LoginViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
