using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class NotificationsPage : BasePage
{
	public NotificationsPage(NotificationsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
