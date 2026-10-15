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
		if (DeviceInfo.Idiom == DeviceIdiom.Phone) MoveDeskItemsToBottomBar();
	}

	// short names fit under a bottom-bar slot; the pages themselves keep their full titles
	static readonly Dictionary<string, string> ShortNames = new()
	{
		["Payments to verify"] = "Verify", ["Counter payment"] = "Counter", ["Check-in"] = "Check-in", ["Amenity visits"] = "Amenities",
	};

	// On a phone the four daily screens stay one tap away along the bottom; admin pages, Alerts and Settings stay in the menu.
	// Windows keeps its locked side menu, which is already always on screen.
	void MoveDeskItemsToBottomBar()
	{
		var bar = new TabBar();
		foreach (var item in Items.Where(i => i.ClassId == "desk").ToList())
		{
			var page = (ShellContent)item.Items[0].Items[0];
			bar.Items.Add(new Tab { Title = ShortNames.GetValueOrDefault(item.Title, item.Title), Items = { new ShellContent { ContentTemplate = page.ContentTemplate } } });
			Items.Remove(item);
		}
		Items.Insert(0, bar);
		CurrentItem = bar;   // the shell chose its first page before the bar existed
	}
}
