#if WINDOWS
using GymMembership.Core.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>
/// The Windows side menu: a slim white strip of black icons, the page name on hover, the current page on lime. Admins get the front desk
/// pages folded under one briefcase that opens downward. Sign out and the profile picture sit at the bottom; the middle scrolls on short
/// windows. Custom content navigates through the original Shell items, so routes, pages and the phone menus stay as they are.
/// </summary>
public static class IconRail
{
    // page title -> icon; every title in the three shells has one (IconRailTests)
    internal static readonly Dictionary<string, string> PageIcons = new()
    {
        ["Overview"] = "layout-dashboard",
        ["Home"] = "house",
        ["Payments to verify"] = "badge-check",
        ["Cashier"] = "banknote",
        ["Check-in"] = "scan-line",
        ["Amenity Status"] = "waves",
        ["Users and roles"] = "users",
        ["Packages and amenities"] = "package",
        ["Discounts"] = "tag",
        ["Cancellations & refunds"] = "undo",
        ["Void a payment"] = "ban",
        ["Gym settings"] = "sliders-horizontal",
        ["Revenue"] = "chart-line",
        ["Logs & Archive"] = "archive",
        ["Messages"] = "message-square",
        ["Settings"] = "settings",
        ["My membership"] = "id-card",
        ["Packages"] = "package",
        ["Amenities"] = "waves",
        ["Book an amenity"] = "calendar-plus",
        ["Coaches"] = "dumbbell",
        ["Sessions"] = "calendar-days",
        ["Time requests"] = "clock",
        ["Dashboard"] = "layout-dashboard",
        ["Trainees"] = "users",
        ["My shifts"] = "calendar-clock",
        ["My coach profile"] = "user-round",
    };

    public static void Install(Shell shell, string roleName, HeaderViewModel header, bool groupFrontDesk)
    {
        var links = new List<(ShellItem Item, Button Button, IconView Icon)>();
        var pages = new VerticalStackLayout { Spacing = 6, HorizontalOptions = LayoutOptions.Center };

        View Link(ShellItem item)
        {
            var (cell, button, icon) = IconCell(PageIcons.GetValueOrDefault(item.Title, "layout-dashboard"), item.Title);
            button.AutomationId = "AdminNavigation-" + item.Route;   // scripts/Test-WindowsAdminNavigation.ps1 drives the menu by these ids
            button.Clicked += (_, _) => shell.CurrentItem = item;
            links.Add((item, button, icon));
            return cell;
        }

        void AddRun(List<View> run)
        {
            if (run.Count == 0) return;
            if (pages.Children.Count > 0) pages.Children.Add(Divider());
            foreach (var view in run) pages.Children.Add(view);
        }

        bool Bottom(ShellItem i) => i.Route is "messages" or "settings";
        var items = shell.Items.ToList();
        var desk = groupFrontDesk ? items.Where(i => i.ClassId == "desk").ToList() : [];
        Border? group = null;
        Button? briefcase = null;
        IconView? briefcaseIcon = null;

        if (groupFrontDesk)
        {
            var first = items.Where(i => i.Route == "overview").Select(Link).ToList();
            if (desk.Count > 0)
            {
                var (cell, button, icon) = IconCell("briefcase", "Front desk operations");
                button.AutomationId = "AdminFrontDeskGroup";
                var deskPages = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(0, 6) };
                foreach (var item in desk) deskPages.Children.Add(Link(item));
                group = new Border { StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 16 }, Padding = new Thickness(4, 0), IsVisible = false, Content = deskPages };
                group.SetAppThemeColor(VisualElement.BackgroundColorProperty, Theme("CardLight"), Theme("SurfaceDark"));
                button.Clicked += (_, _) => { group.IsVisible = !group.IsVisible; Refresh(); };
                (briefcase, briefcaseIcon) = (button, icon);
                first.Add(cell);
                first.Add(group);
            }
            AddRun(first);
            AddRun(items.Where(i => i.ClassId is "admin" or "super").Select(Link).ToList());
        }
        else AddRun(items.Where(i => !Bottom(i)).Select(Link).ToList());
        AddRun(items.Where(Bottom).Select(Link).ToList());

        void Refresh()
        {
            foreach (var (item, button, icon) in links)
            {
                var current = shell.CurrentItem == item;
                icon.Selected = current;
                SemanticProperties.SetDescription(button, current ? item.Title + ", current page" : item.Title);
            }
            if (group is null || briefcase is null || briefcaseIcon is null) return;
            var currentDesk = desk.FirstOrDefault(i => shell.CurrentItem == i);
            var deskCurrent = currentDesk is not null;
            briefcaseIcon.Selected = deskCurrent && !group.IsVisible;   // folded away, the briefcase stands in for the page you are on
            SemanticProperties.SetDescription(briefcase, "Front desk operations, " + (group.IsVisible ? "expanded. Collapse group" : "collapsed. Expand group")
                + (deskCurrent ? ", current page: " + currentDesk!.Title : ""));
            ToolTipProperties.SetText(briefcase, group.IsVisible ? "Hide front desk pages" : "Front desk operations");
        }

        // top: the logo, which also says whose menu this is
        var logo = new ContentView { Content = new DumbbellLogo { Size = 40 }, HorizontalOptions = LayoutOptions.Center };
        ToolTipProperties.SetText(logo, "Gym Membership, " + roleName);
        SemanticProperties.SetDescription(logo, "Gym Membership, " + roleName);

        // bottom: Sign out, then your picture (opens Settings, as the header picture does on phones)
        var (signOutCell, signOut, _) = IconCell("log-out", "Sign out");
        signOut.Command = header.SignOutCommand;
        var avatar = new Avatar { Diameter = 36, HorizontalOptions = LayoutOptions.Center, BindingContext = header };
        avatar.SetBinding(Avatar.LettersProperty, static (HeaderViewModel vm) => vm.Letters);
        avatar.SetBinding(Avatar.UrlProperty, static (HeaderViewModel vm) => vm.AvatarUrl);
        var settings = items.FirstOrDefault(i => i.Route == "settings");
        avatar.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => { if (settings is not null) shell.CurrentItem = settings; }) });
        ToolTipProperties.SetText(avatar, "Settings");
        SemanticProperties.SetDescription(avatar, "Your profile picture. Opens Settings.");

        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], RowSpacing = 12 };
        layout.Add(logo, 0, 0);
        layout.Add(new ScrollView { Content = pages, VerticalScrollBarVisibility = ScrollBarVisibility.Never }, 0, 1);
        layout.Add(new VerticalStackLayout { Spacing = 10, Children = { signOutCell, avatar } }, 0, 2);

        var rail = new Border
        {
            StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 22 },
            Margin = new Thickness(12), Padding = new Thickness(0, 14), WidthRequest = 64, Content = layout
        };
        rail.SetAppThemeColor(VisualElement.BackgroundColorProperty, Theme("RailLight"), Theme("RailDark"));

        shell.FlyoutHeader = null;
        shell.FlyoutFooter = null;
        shell.FlyoutWidth = 88;
        shell.SetAppThemeColor(Shell.FlyoutBackgroundColorProperty, Theme("BackdropLight"), Theme("BackdropDark"));
        shell.FlyoutContent = rail;
        shell.Navigated += (_, _) => Refresh();
        Refresh();
    }

    // a 48 x 48 click target (real Button: keyboard, screen reader) with the icon drawn on top; the page name shows on hover
    static (Grid Cell, Button Button, IconView Icon) IconCell(string icon, string name)
    {
        var button = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0, WidthRequest = 48, HeightRequest = 48, CornerRadius = 14 };
        ToolTipProperties.SetText(button, name);
        SemanticProperties.SetDescription(button, name);
        var view = new IconView { Icon = icon, Size = 18, ChipSize = 38, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        return (new Grid { WidthRequest = 48, HeightRequest = 48, HorizontalOptions = LayoutOptions.Center, Children = { button, view } }, button, view);
    }

    static BoxView Divider()
    {
        var line = new BoxView { HeightRequest = 1.5, WidthRequest = 28, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 4) };
        line.SetAppThemeColor(BoxView.ColorProperty, Theme("LineLight"), Theme("LineDark"));
        return line;
    }

    static Color Theme(string key) => (Color)Application.Current!.Resources[key];
}
#endif
