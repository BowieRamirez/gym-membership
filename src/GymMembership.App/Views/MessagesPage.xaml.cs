using GymMembership.Core.ViewModels;
using Microsoft.Maui.Controls;

namespace GymMembership.App.Views;

public partial class MessagesPage : BasePage
{
	public MessagesPage(MessengerViewModel vm)
	{
		InitializeComponent();
		// phone: the list first, then the chat on its own screen; desktop: both side by side
		if (DeviceInfo.Idiom == DeviceIdiom.Phone)
		{
			vm.IsCompact = true;
			Root.ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star));
			Grid.SetColumn(ChatPane, 0);
		}
		vm.UiContext = SynchronizationContext.Current;   // the page is built on the UI thread; Realtime calls back on another
		BindingContext = vm;
	}

	// new messages arrive instantly through Realtime; this timer is the safety net (slower while connected)
	protected override TimeSpan? AutoRefresh => (BindingContext as MessengerViewModel)?.RefreshEvery ?? TimeSpan.FromSeconds(6);

	// a coach's first tab says "Trainees"; the page is the same in both shells
	protected override void OnAppearing()
	{
		if (BindingContext is MessengerViewModel vm) vm.IsCoach = Shell.Current is CoachShell;
		base.OnAppearing();
	}

	void OnOpen(object? sender, TappedEventArgs e) => Row<MessengerViewModel>(sender, v => v.OpenCommand);
	void OnOpenPinned(object? sender, TappedEventArgs e) { if (BindingContext is MessengerViewModel { Pinned: { } item } vm) vm.OpenCommand.Execute(item); }
	void OnOpenRequests(object? sender, EventArgs e) => (BindingContext as MessengerViewModel)?.OpenRequestsCommand.Execute(null);
	void OnOpenHit(object? sender, TappedEventArgs e) => Row<MessengerViewModel>(sender, v => v.OpenHitCommand);
}
