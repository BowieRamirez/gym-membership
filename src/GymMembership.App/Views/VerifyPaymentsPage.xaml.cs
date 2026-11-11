using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class VerifyPaymentsPage : BasePage
{
	public VerifyPaymentsPage(VerifyPaymentsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// F4: a payment sent or verified elsewhere shows up here by itself; no timer (a hint, or Refresh)
	protected override bool LiveRecords => true;
	protected override Controls.UpdatedBar? Updated => UpdatedLine;

	void OnViewProof(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.ViewProofCommand);
	void OnApprove(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.ApproveCommand);
	void OnReject(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.RejectCommand);
	void OnApproveClaim(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.ApproveClaimCommand);
	void OnDeclineClaim(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.DeclineClaimCommand);
}
