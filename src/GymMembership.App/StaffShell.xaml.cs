using GymMembership.Core.Services;

namespace GymMembership.App;

public partial class StaffShell : Shell
{
	public StaffShell(AppRole role, IServiceProvider sp)
	{
		InitializeComponent();
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
		RoleLabel.Text = role == AppRole.Admin ? "Admin" : "Front desk";
		if (role != AppRole.Admin)
			foreach (var item in Items.Where(i => i.ClassId == "admin").ToList())
				Items.Remove(item);
	}
}
