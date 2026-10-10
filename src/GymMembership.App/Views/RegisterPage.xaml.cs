using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RegisterPage : BasePage
{
	public RegisterPage(RegisterViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
