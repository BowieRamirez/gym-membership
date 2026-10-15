using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachesPage : BasePage
{
	public CoachesPage(CoachesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnHire(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.HireCommand);
	void OnMessage(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.MessageCommand);
	void OnEndHire(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.EndHireCommand);
}
