using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class ShiftsPage : BasePage
{
	public ShiftsPage(ShiftsViewModel vm, TraineesPage trainees)
	{
		InitializeComponent();
		BindingContext = vm;
		HostBelow(TraineesHost, trainees);   // owner, 2026-11-12: the trainees live here now, under Upcoming shifts
	}

	void OnCancel(object? sender, EventArgs e) => Row<ShiftsViewModel>(sender, v => v.CancelCommand);
}
