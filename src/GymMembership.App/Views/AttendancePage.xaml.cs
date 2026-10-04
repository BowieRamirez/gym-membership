using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AttendancePage : BasePage
{
	public AttendancePage(AttendanceViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
