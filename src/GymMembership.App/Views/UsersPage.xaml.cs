using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class UsersPage : BasePage
{
	public UsersPage(UsersViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
