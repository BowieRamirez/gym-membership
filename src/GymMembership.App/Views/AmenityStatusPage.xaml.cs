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
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds((BindingContext as AmenityStatusViewModel)?.HasWaitingBalance == true ? 5 : 30);   // 0141: quick while a balance is being paid online
	void OnChange(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.SelectCommand);
	void OnViewBooked(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.ViewCommand);   // the Booked card
	void OnTakeCash(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.TakeBalanceCommand);         // G13
	void OnTakeOnline(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.TakeBalanceOnlineCommand);   // 0141
	void OnApproveClaim(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.ApproveClaimCommand);   // F13, cashier plan Y7
	void OnDeclineClaim(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.DeclineClaimCommand);
	void OnMaintenance(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Maintenance");
	void OnCleaning(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Cleaning");
	void OnClosed(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Closed");
}
