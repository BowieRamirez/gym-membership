using CommunityToolkit.Maui.Views;
using GymMembership.Core.Models;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

/// <summary>The alerts dropdown opened by the header bell. Replaces the old Alerts page.</summary>
public partial class NotificationPanel : Popup
{
	public NotificationPanel(NotificationsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	async void OnClose(object? sender, EventArgs e) => await CloseAsync();

	// a plain handler, not a RelativeSource command binding: those threw NullReferenceException while the list reloaded
	void OnTapped(object? sender, TappedEventArgs e)
	{
		if ((sender as BindableObject)?.BindingContext is UserNotification row) ((NotificationsViewModel)BindingContext).MarkReadCommand.Execute(row);
	}
}
