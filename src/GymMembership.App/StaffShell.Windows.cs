#if WINDOWS
namespace GymMembership.App;

public partial class StaffShell
{
	void SetupAdminNavigation()
	{
		var menu = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(12, 8) };
		var operations = new VerticalStackLayout { Spacing = 4, Padding = new Thickness(16, 0, 0, 0), IsVisible = false };
		var toggle = MenuButton("Front desk operations  ▸");
		toggle.FontFamily = "BarlowSemiBold";
		toggle.FontSize = 18;
		toggle.AutomationId = "AdminFrontDeskGroup";
		menu.Children.Add(toggle);
		menu.Children.Add(operations);
		var administration = new Label
		{
			Text = "Administration", FontFamily = toggle.FontFamily, FontSize = toggle.FontSize,
			MinimumHeightRequest = 48, VerticalTextAlignment = TextAlignment.Center,
			Margin = new Thickness(12, 0, 0, 0)
		};
		administration.SetAppThemeColor(Label.TextColorProperty, ThemeColor("InkLight"), ThemeColor("InkDark"));
		menu.Children.Add(administration);
		var administrationPages = new VerticalStackLayout { Spacing = operations.Spacing, Padding = operations.Padding };
		menu.Children.Add(administrationPages);

		var links = new Dictionary<ShellItem, Button>();
		var deskItems = new HashSet<ShellItem>();
		foreach (var item in Items)
		{
			var button = MenuButton(item.Title);
			button.AutomationId = "AdminNavigation-" + item.Route;
			button.Clicked += (_, _) => CurrentItem = item;
			links.Add(item, button);
			if (item.Route == "overview")
			{
				menu.Children.Insert(0, button);
			}
			else if (item.ClassId == "desk" || item.Route == "messages")
			{
				deskItems.Add(item);
				operations.Children.Add(button);
			}
			else if (item.ClassId == "admin")
			{
				administrationPages.Children.Add(button);
			}
			else
			{
				menu.Children.Add(button);
			}
		}

		void Refresh()
		{
			foreach (var (item, button) in links)
			{
				var selected = CurrentItem == item;
				button.FontFamily = selected ? "BarlowSemiBold" : "Barlow";
				button.SetAppThemeColor(Button.BackgroundColorProperty,
					selected ? ThemeColor("LineLight") : Colors.Transparent,
					selected ? ThemeColor("LineDark") : Colors.Transparent);
				SemanticProperties.SetDescription(button, selected ? item.Title + ", current page" : item.Title);
			}
			var current = CurrentItem is not null && deskItems.Contains(CurrentItem) ? ", current page: " + CurrentItem.Title : "";
			toggle.Text = operations.IsVisible ? "Front desk operations  ▾" : "Front desk operations  ▸";
			SemanticProperties.SetDescription(toggle, "Front desk operations, " + (operations.IsVisible ? "expanded. Collapse group" : "collapsed. Expand group") + current);
			ToolTipProperties.SetText(toggle, operations.IsVisible ? "Hide front desk pages" : "Show front desk pages");
		}

		toggle.Clicked += (_, _) => { operations.IsVisible = !operations.IsVisible; Refresh(); };
		Navigated += (_, _) => Refresh();
		// Shell owns its native menu and selection; custom content navigates through the original Shell items.
		FlyoutContent = new ScrollView { Content = menu };
		Refresh();
	}

	static Color ThemeColor(string key) => (Color)Application.Current!.Resources[key];

	static Button MenuButton(string title)
	{
		var button = new Button
		{
			Text = title, FontFamily = "Barlow", FontSize = 16,
			BackgroundColor = Colors.Transparent, BorderWidth = 0,
			Padding = new Thickness(12, 8), MinimumHeightRequest = 48,
			CornerRadius = 6, HorizontalOptions = LayoutOptions.Fill
		};
		button.SetAppThemeColor(Button.TextColorProperty, ThemeColor("InkLight"), ThemeColor("InkDark"));
		button.HandlerChanged += (_, _) =>
		{
			if (button.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.Button native)
				native.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
		};
		return button;
	}
}
#endif
