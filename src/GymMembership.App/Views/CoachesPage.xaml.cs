using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachesPage : BasePage
{
	public CoachesPage(CoachesViewModel vm, AmenitiesPage amenities, Core.Services.IMembershipService membership)
	{
		InitializeComponent();
		BindingContext = vm;
		HostPart(Switch, Part, amenities);
		// Amenities stays members-only (it was its own locked menu item): a guest gets the same notice in that half
		Dispatcher.Dispatch(async () =>
		{
			if (await membership.AccessAsync() is { Ok: true, Value: { Active: false } access })
				HostPart(Switch, Part, new LockedPage("Amenities", access.PaymentPending));
		});
	}

	// D9: a coach appears or disappears within 30 seconds of checking in or out
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);

	void OnHire(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.HireCommand);
	void OnMessage(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.MessageCommand);
	void OnEndHire(object? sender, EventArgs e) => Row<CoachesViewModel>(sender, v => v.EndHireCommand);
}
