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
		BindingContext = vm;
	}

	// new messages arrive by looking again every few seconds while the page is open (Realtime comes later)
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(6);

	void OnOpen(object? sender, TappedEventArgs e) => Row<MessengerViewModel>(sender, v => v.OpenCommand);
	void OnOpenHit(object? sender, TappedEventArgs e) => Row<MessengerViewModel>(sender, v => v.OpenHitCommand);
}
