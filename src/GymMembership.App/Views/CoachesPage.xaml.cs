using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachesPage : BasePage
{
	public CoachesPage(CoachesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// D9: a coach appears or disappears within 30 seconds of checking in or out
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);

	void OnHire(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.HireCommand);
	void OnMessage(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.MessageCommand);
	void OnEndHire(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.EndHireCommand);
}
