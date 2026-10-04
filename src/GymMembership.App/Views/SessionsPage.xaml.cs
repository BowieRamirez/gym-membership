using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class SessionsPage : BasePage
{
	public SessionsPage(SessionsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
