using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AttendancePage : BasePage
{
	public AttendancePage(AttendanceViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
		if (DeviceInfo.Idiom == DeviceIdiom.Phone) vm.ViewMode = "Week";   // D10: a phone opens the calendar on the week
	}

	void OnDay(object? sender, TappedEventArgs e) => Row<AttendanceViewModel>(sender, v => v.SelectDayCommand);
	void OnWeekDay(object? sender, TappedEventArgs e) => Row<AttendanceViewModel>(sender, v => v.SelectDayCommand, row => ((WeekRow)row).Day);
	void OnCancelShift(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.CancelShiftCommand);

	void OnMarkPresent(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|present");
	void OnMarkLate(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|late");
	void OnMarkAbsent(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|absent");
	void OnMarkExcused(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|excused");
}
