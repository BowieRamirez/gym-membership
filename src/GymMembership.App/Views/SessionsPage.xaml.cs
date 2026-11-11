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
		if (CalendarHost.Content is null && Shell.Current is MemberShell)
		{
			HostBelow(CalendarHost, _services.GetRequiredService<MemberCalendarPage>());
			if (DeviceInfo.Idiom == DeviceIdiom.Phone)
			{
				// the calendar sits right under its button, closed until asked for
				var stack = (VerticalStackLayout)CalendarSection.Parent;
				stack.Remove(CalendarSection);
				stack.Insert(stack.IndexOf(CalendarToggle) + 1, CalendarSection);
				CalendarToggle.IsVisible = true;
			}
			else CalendarSection.IsVisible = true;
		}
		base.OnAppearing();
	}

	void OnToggleCalendar(object? sender, EventArgs e)
	{
		CalendarSection.IsVisible = !CalendarSection.IsVisible;
		CalendarToggle.Text = CalendarSection.IsVisible ? "Hide calendar" : "Show calendar";
	}

	protected override TimeSpan? AutoRefresh => CalendarSection.IsVisible ? TimeSpan.FromSeconds(60) : null;
	protected override bool RefreshOwnViewModel => false;

	void OnExtend(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.ExtendCommand);
	void OnComplete(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.CompleteCommand);
	void OnNoShow(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.NoShowCommand);
	void OnStartReschedule(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.StartRescheduleCommand);
	void OnCancel(object? sender, EventArgs e) => Row<SessionsViewModel>(sender, v => v.CancelCommand);
}
