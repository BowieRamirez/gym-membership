using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AttendancePage : BasePage
{
	public AttendancePage(AttendanceViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnMarkPresent(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((SessionRow)row).Session.Id}|present");
	void OnMarkLate(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((SessionRow)row).Session.Id}|late");
	void OnMarkAbsent(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((SessionRow)row).Session.Id}|absent");
	void OnMarkExcused(object? sender, EventArgs e) => Row<AttendanceViewModel>(sender, v => v.MarkCommand, row => $"{((SessionRow)row).Session.Id}|excused");
}
