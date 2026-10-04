using GymMembership.Core.Services;

namespace GymMembership.App;

public partial class StaffShell : Shell
{
	public StaffShell(AppRole role)
	{
		InitializeComponent();
		RoleLabel.Text = role == AppRole.Admin ? "Admin" : "Front desk";
		if (role != AppRole.Admin)
			foreach (var item in Items.Where(i => i.ClassId == "admin").ToList())
				Items.Remove(item);
	}
}
