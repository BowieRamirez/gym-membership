using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class MemberCalendarPage : BasePage
{
	public MemberCalendarPage(MemberCalendarViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
		vm.Days.CollectionChanged += (_, _) => FitMonth();
	}

	// ponytail: the wrapped month keeps the height it measured before the tiles got their width (a blank band below it on phones),
	// so it gets its exact height: weeks x (tile + 4 px margin). Drop this if FlexLayout ever re-measures on its own.
	void FitMonth() => Month.HeightRequest = Math.Ceiling(((MemberCalendarViewModel)BindingContext).Days.Count / 7.0) * ((DeviceInfo.Idiom == DeviceIdiom.Phone ? 52 : 86) + 4);

	// a coach's new open time or a payment approved at the desk shows up without reopening the page
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(60);

	void OnMonthSized(object? sender, EventArgs e) { if (Month.Width > 0) CalendarContent.Resources["TileWidth"] = Math.Floor(Month.Width / 7) - 4; }   // 2 px margin each side

	void OnDay(object? sender, EventArgs e) => Row<MemberCalendarViewModel>(sender, v => v.PickDayCommand);
	void OnRequest(object? sender, EventArgs e) => Row<MemberCalendarViewModel>(sender, v => v.RequestCommand);
	void OnOpen(object? sender, EventArgs e) => Row<MemberCalendarViewModel>(sender, v => v.OpenCommand);
}
