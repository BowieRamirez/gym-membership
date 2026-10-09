using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class MessagesPage : BasePage
{
	public MessagesPage(MessagesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
