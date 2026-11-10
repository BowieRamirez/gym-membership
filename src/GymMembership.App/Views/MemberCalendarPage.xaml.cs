using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class MemberCalendarPage : BasePage
{
	public MemberCalendarPage(MemberCalendarViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// a coach's new open time or a payment approved at the desk shows up without reopening the page
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(60);

	void OnMonthSized(object? sender, EventArgs e) { if (Month.Width > 0) CalendarContent.Resources["TileWidth"] = Math.Floor(Month.Width / 7) - 4; }   // 2 px margin each side

	void OnDay(object? sender, EventArgs e) => Row<MemberCalendarViewModel>(sender, v => v.PickDayCommand);
	void OnRequest(object? sender, EventArgs e) => Row<MemberCalendarViewModel>(sender, v => v.RequestCommand);
	void OnOpen(object? sender, EventArgs e) => Row<MemberCalendarViewModel>(sender, v => v.OpenCommand);
}
