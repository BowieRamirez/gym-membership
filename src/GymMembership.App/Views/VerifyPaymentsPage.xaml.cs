using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class VerifyPaymentsPage : BasePage
{
	public VerifyPaymentsPage(VerifyPaymentsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnViewProof(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.ViewProofCommand);
	void OnApprove(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.ApproveCommand);
	void OnReject(object? sender, EventArgs e) => Row<VerifyPaymentsViewModel>(sender, v => v.RejectCommand);
}
