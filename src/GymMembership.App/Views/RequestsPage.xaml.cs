using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RequestsPage : BasePage
{
	public RequestsPage(RequestsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnApprove(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.ApproveCommand);
	void OnReject(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.RejectCommand);
	void OnCancel(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.CancelCommand);
}
