using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AmenityStatusPage : BasePage
{
	public AmenityStatusPage(AmenityStatusViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// the reload only refills the list of cards; it never touches the editor fields, so typing is safe
	// every 5 seconds while a walk-in is paying online, so the receipt and the Booked card follow by themselves
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds((BindingContext as AmenityStatusViewModel)?.Booking?.IsWaiting == true ? 5 : 30);

	void OnPickCustomer(object? sender, EventArgs e)
	{
		if (BindingContext is AmenityStatusViewModel { Booking: { } b } && (sender as BindableObject)?.BindingContext is GymMembership.Core.Services.MemberHit hit) b.PickMemberCommand.Execute(hit);
	}
	void OnCloseBooking(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).IsBooking = false;
	void OnChange(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.SelectCommand);
	void OnViewBooked(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.ViewCommand);   // the Booked card
	void OnTakeCash(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.TakeBalanceCommand);         // G13
	void OnTakeGcash(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.TakeBalanceGcashCommand);
	void OnApproveClaim(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.ApproveClaimCommand);   // F13, cashier plan Y7
	void OnDeclineClaim(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.DeclineClaimCommand);
	void OnMaintenance(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Maintenance");
	void OnCleaning(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Cleaning");
	void OnClosed(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Closed");
}
