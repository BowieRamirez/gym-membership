using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class VerifyAmenityUsagesPage : BasePage
{
	public VerifyAmenityUsagesPage(VerifyAmenityUsagesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnApprove(object? sender, EventArgs e) => Row<VerifyAmenityUsagesViewModel>(sender, v => v.ApproveCommand);
	void OnReject(object? sender, EventArgs e) => Row<VerifyAmenityUsagesViewModel>(sender, v => v.RejectCommand);
}
