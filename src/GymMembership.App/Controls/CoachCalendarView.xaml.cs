using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

public partial class CoachCalendarView : ContentView
{
	public CoachCalendarView() => InitializeComponent();

	protected override void OnBindingContextChanged()
	{
		base.OnBindingContextChanged();
		if (BindingContext is AttendanceViewModel vm && DeviceInfo.Idiom == DeviceIdiom.Phone) vm.ViewMode = "Week";   // D10: a phone opens the calendar on the week
	}

	void Run<T>(object? sender, Func<AttendanceViewModel, CommunityToolkit.Mvvm.Input.IRelayCommand<T>> cmd, Func<object, T>? map = null)
	{
		if (BindingContext is not AttendanceViewModel vm || (sender as BindableObject)?.BindingContext is not { } row) return;
		var arg = map is null ? (T)row : map(row);
		var command = cmd(vm);
		if (command.CanExecute(arg)) command.Execute(arg);
	}

	void OnDay(object? sender, TappedEventArgs e) => Run<CalendarDay>(sender, v => v.SelectDayCommand);
	void OnWeekDay(object? sender, TappedEventArgs e) => Run<CalendarDay>(sender, v => v.SelectDayCommand, row => ((WeekRow)row).Day);
	void OnCancelShift(object? sender, EventArgs e) => Run<Core.Models.ShiftPost>(sender, v => v.CancelShiftCommand);

	void OnMarkPresent(object? sender, EventArgs e) => Run<string>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|present");
	void OnMarkLate(object? sender, EventArgs e) => Run<string>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|late");
	void OnMarkAbsent(object? sender, EventArgs e) => Run<string>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|absent");
	void OnMarkExcused(object? sender, EventArgs e) => Run<string>(sender, v => v.MarkCommand, row => $"{((CalendarSession)row).Session.Id}|excused");
}
