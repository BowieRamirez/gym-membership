using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RequestsPage : BasePage
{
	public RequestsPage(RequestsViewModel vm)
	{
		InitializeComponent();
#if WINDOWS
		vm.UseWindowsRequestForm = true;
#endif
		BindingContext = vm;
	}

	void OnApprove(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.ApproveCommand);
	void OnAccept(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.AcceptCommand);
	void OnPropose(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.StartProposeCommand);
	void OnAnother(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.StartAnotherCommand);
	void OnDecline(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.StartDeclineCommand);
	void OnCancel(object? sender, EventArgs e) => Row<RequestsViewModel>(sender, v => v.CancelCommand);
}
