using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class TraineesPage : BasePage
{
	public TraineesPage(TraineesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnMessage(object? sender, EventArgs e) => Row<TraineesViewModel>(sender, v => v.MessageCommand);
	void OnRemove(object? sender, EventArgs e) => Row<TraineesViewModel>(sender, v => v.RemoveCommand);
}
