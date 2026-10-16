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
		["Home"] = "Home", ["Payments to verify"] = "Verify", ["Cashier"] = "Cashier", ["Check-in"] = "Check-in", ["Amenity Status"] = "Amenities",
	};

	// On a phone the four daily screens stay one tap away along the bottom; admin pages, Alerts and Settings stay in the menu.
	// Windows keeps its locked side menu, which is already always on screen.
	void MoveDeskItemsToBottomBar()
	{
		var bar = new TabBar();
		foreach (var item in Items.Where(i => i.ClassId == "desk").ToList())
		{
			var page = (ShellContent)item.Items[0].Items[0];
			bar.Items.Add(new Tab { Title = ShortNames.GetValueOrDefault(item.Title, item.Title), Route = item.Route, Items = { new ShellContent { ContentTemplate = page.ContentTemplate } } });
			Items.Remove(item);
		}
		Items.Insert(0, bar);
		CurrentItem = bar;   // the shell chose its first page before the bar existed
	}

	// A Home tile opens the screen it counts. Works for the phone's bottom bar and for the side menu alike, because it looks the route up
	// on both levels instead of asking the shell to resolve a path.
	public void ShowDeskPage(string route)
	{
		foreach (var item in Items)
		{
			if (item.Route == route) { CurrentItem = item; return; }
			foreach (var section in item.Items)
				if (section.Route == route) { CurrentItem = item; item.CurrentItem = section; return; }
		}
	}
}
