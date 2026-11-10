using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class SessionsPage : BasePage
{
	readonly IServiceProvider _services;

	public SessionsPage(SessionsViewModel vm, IServiceProvider services)
	{
		InitializeComponent();
		BindingContext = vm;
		_services = services;
	}

	protected override void OnAppearing()
	{
#if WINDOWS
		if (!CalendarSection.IsVisible && Shell.Current is MemberShell)
		{
			HostBelow(CalendarHost, _services.GetRequiredService<MemberCalendarPage>());
			CalendarSection.IsVisible = true;
		}
#endif
		base.OnAppearing();
	}

	protected override TimeSpan? AutoRefresh => CalendarSection.IsVisible ? TimeSpan.FromSeconds(60) : null;
	protected override bool RefreshOwnViewModel => false;

	void OnExtend(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.ExtendCommand);
	void OnComplete(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.CompleteCommand);
	void OnNoShow(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.NoShowCommand);
	void OnStartReschedule(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.StartRescheduleCommand);
	void OnCancel(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.CancelCommand);
}
