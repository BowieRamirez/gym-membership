using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class NotificationsPage : BasePage
{
	public NotificationsPage(NotificationsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnMarkRead(object? sender, TappedEventArgs e) => Row<NotificationsViewModel>(sender, v => v.MarkReadCommand);
}
