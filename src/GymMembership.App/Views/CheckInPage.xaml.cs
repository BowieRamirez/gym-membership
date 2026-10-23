using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CheckInPage : BasePage
{
	public CheckInPage(CheckInViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnCheckIn(object? sender, EventArgs e) => Row<CheckInViewModel>(sender, v => v.CheckInCommand);
	void OnToggleCoach(object? sender, EventArgs e) => Row<CheckInViewModel>(sender, v => v.ToggleCoachCommand);
}
