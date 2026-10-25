using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class SessionsPage : BasePage
{
	public SessionsPage(SessionsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnExtend(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.ExtendCommand);
	void OnComplete(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.CompleteCommand);
	void OnNoShow(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.NoShowCommand);
	void OnStartReschedule(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.StartRescheduleCommand);
	void OnCancel(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.CancelCommand);
}
