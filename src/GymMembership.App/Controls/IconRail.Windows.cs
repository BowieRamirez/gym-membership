#if WINDOWS
using CommunityToolkit.Mvvm.Messaging;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>
/// The Windows side menu. Front desk, coach and member: a full-height yellow (light) or near-black (dark) strip of icons, the page name on
/// hover, the current page on a black or yellow square. System admin and Admin (Fix 4, W1): the same strip widened to 240 with words,
/// grouped as AdminMenu says, folds for Front desk, Attendance and Payroll, and a Pending tag on Attendance Review. Sign out and the profile
/// picture sit at the bottom; the middle scrolls on short windows. Custom content navigates through the original Shell items, so routes,
/// pages and the phone menus stay as they are.
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
        ["Membership checking"] = "scan-line",
        ["Amenity status"] = "waves",
        ["Users and roles"] = "users",
        ["Packages and amenities"] = "package",
        ["Discounts"] = "tag",
        ["Refunds"] = "undo",
        ["Gym settings"] = "sliders-horizontal",
        ["Revenue"] = "chart-line",
        ["Payroll"] = "wallet",
        ["Scheduling"] = "calendar-clock",
        ["Attendance"] = "clipboard-check",
        ["Damage reports"] = "alert-circle",
        ["Announcements"] = "megaphone",
        ["Logs & Archive"] = "archive",
        ["Messages"] = "message-square",
        ["Settings"] = "settings",
        ["My membership"] = "id-card", ["Payments"] = "receipt",
        ["Packages"] = "package", ["Renew or change"] = "package",
        ["Amenities"] = "waves",
        ["Book an amenity"] = "calendar-plus",
        ["Coaches"] = "dumbbell",
        ["Sessions"] = "calendar-days",
        ["Dashboard"] = "layout-dashboard",
        ["My shifts"] = "calendar-clock",
    };

    static bool Bottom(ShellItem i) => i.Route is "messages" or "settings";

    /// <summary>The icon strip (front desk, coach, member).</summary>
    public static void Install(Shell shell, string roleName, HeaderViewModel header)
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

        var items = shell.Items.ToList();
        AddRun(items.Where(i => !Bottom(i)).Select(Link).ToList());
        AddRun(items.Where(Bottom).Select(Link).ToList());

        void Refresh()
        {
            foreach (var (item, button, icon) in links)
            {
                var current = shell.CurrentItem == item;
                icon.Selected = current;
                SemanticProperties.SetDescription(button, current ? item.Title + ", current page" : item.Title);
            }
        }

        var logo = Logo(roleName);
        logo.HorizontalOptions = LayoutOptions.Center;
        var (signOutCell, signOut, _) = IconCell("log-out", "Sign out");
        signOut.Command = header.SignOutCommand;
        var avatar = Picture(shell, header, items);
        avatar.HorizontalOptions = LayoutOptions.Center;
        Mount(shell, 72, logo, pages, new VerticalStackLayout { Spacing = 10, Children = { signOutCell, avatar } }, Refresh);
    }

    /// <summary>Fix 4 (W1, W3): the System admin's and Admin's menu with words. <paramref name="pending"/> reads the Pending count for Attendance Review.</summary>
    public static void InstallMenu(Shell shell, string roleName, HeaderViewModel header, Func<Task<int>> pending)
    {
        var items = shell.Items.ToList();
        ShellItem? Find(string? title) => title is null ? null : items.FirstOrDefault(i => i.Title == title && !Bottom(i));
        var links = new List<(AdminMenuEntry Entry, ShellItem Item, MenuRow Row)>();
        var folds = new List<(AdminMenuEntry Entry, MenuRow Row, VerticalStackLayout Kids)>();
        string? lastTab = null;
        ShellItem? lastItem = null;
        (ShellItem Item, string Tab)? wantTab = null;
        StatusChip? pendingChip = null;

        void ApplyTab()
        {
            if (wantTab is not { } w || shell.CurrentItem != w.Item || shell.CurrentPage?.BindingContext is not IOpensTab page) return;
            if (!ReferenceEquals(shell.CurrentPage, (w.Item.CurrentItem?.CurrentItem as IShellContentController)?.Page)) return;   // not that page yet
            wantTab = null;
            page.OpenTab(w.Tab);
        }

        MenuRow LinkRow(AdminMenuEntry entry, ShellItem item, bool child)
        {
            var row = new MenuRow(entry.Icon, entry.Label, child);
            row.Button.AutomationId = "AdminNavigation-" + (item.Route + (entry.Tab is { } t ? "-" + t : ""));   // scripts/Test-WindowsAdminNavigation.ps1
            row.Button.Clicked += (_, _) =>
            {
                (lastItem, lastTab) = (item, entry.Tab);
                // the menu's tab (Users and roles: everyone, Employees: staff only) goes to the page once the shell has shown it; a page
                // opened for the first time is built during navigation, so it's applied in Navigated, or now when it is already showing
                wantTab = (item, entry.Tab ?? "");
                if (shell.CurrentItem == item) ApplyTab();
                else shell.CurrentItem = item;
                Refresh();
            };
            links.Add((entry, item, row));
            return row;
        }

        var body = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(12, 0) };
        foreach (var section in AdminMenu.Sections)
        {
            var rows = new List<View>();
            foreach (var entry in section.Entries)
            {
                if (entry.Children is null)
                {
                    if (Find(entry.Page) is { } item) rows.Add(LinkRow(entry, item, child: false));
                    continue;
                }
                var kids = new VerticalStackLayout { Spacing = 2, IsVisible = false };
                foreach (var c in entry.Children)
                    if (Find(c.Page) is { } item)
                    {
                        var row = LinkRow(c, item, child: true);
                        if (c.Page == "Attendance" && c.Tab == "review") { pendingChip = new StatusChip { Status = "Pending", IsVisible = false }; row.Trailing(pendingChip); }
                        kids.Children.Add(row);
                    }
                if (kids.Children.Count == 0) continue;
                var fold = new MenuRow(entry.Icon, entry.Label, child: false, chevron: true);
                if (entry.Label == "Front desk") fold.Button.AutomationId = "AdminFrontDeskGroup";   // the UI script opens and closes it by this id
                fold.Button.Clicked += (_, _) => { kids.IsVisible = !kids.IsVisible; Refresh(); };
                folds.Add((entry, fold, kids));
                rows.Add(fold);
                rows.Add(kids);
            }
            if (rows.Count == 0) continue;
            body.Children.Add(Caption(section.Title, top: body.Children.Count > 0));
            foreach (var r in rows) body.Children.Add(r);
        }
        var bottom = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(12, 0) };
        foreach (var item in items.Where(Bottom)) bottom.Children.Add(LinkRow(new AdminMenuEntry(item.Title, PageIcons.GetValueOrDefault(item.Title, "settings"), item.Title), item, child: false));
        var signOut = new MenuRow("log-out", "Sign out", child: false);
        signOut.Button.Command = header.SignOutCommand;
        bottom.Children.Add(signOut);

        void Refresh()
        {
            foreach (var (entry, item, row) in links)
            {
                // a page with one entry is lit whenever it shows; Payroll, Attendance and Users and roles / Employees light the entry clicked
                // opened some other way (an Overview card, a Home tile): its first entry
                var current = shell.CurrentItem == item
                    && (links.Count(l => l.Item == item) == 1
                        || (lastItem == item ? entry.Tab == lastTab : links.First(l => l.Item == item).Entry == entry));
                row.Selected = current;
                SemanticProperties.SetDescription(row.Button, current ? entry.Label + ", current page" : entry.Label);
            }
            foreach (var (entry, fold, kids) in folds)
            {
                var inside = links.FirstOrDefault(l => l.Row.Selected && kids.Children.Contains(l.Row));
                fold.Selected = inside.Row is not null && !kids.IsVisible;   // folded away, the fold stands in for the page you are on
                SemanticProperties.SetDescription(fold.Button, entry.Label + (kids.IsVisible ? ", expanded. Collapse group" : ", collapsed. Expand group")
                    + (inside.Row is not null ? ", current page: " + inside.Entry.Label : ""));
            }
        }

        async void RefreshPending(int? known = null)
        {
            if (pendingChip is null) return;
            try
            {
                var n = known ?? await pending();
                pendingChip.IsVisible = n > 0;
                SemanticProperties.SetDescription(pendingChip, $"{n} pending");
                ToolTipProperties.SetText(pendingChip, n == 1 ? "1 record to review" : $"{n} records to review");
            }
            catch { pendingChip.IsVisible = false; }   // the tag is a hint; the page itself shows the count
        }
        WeakReferenceMessenger.Default.Register<AttendancePendingChanged>(shell, (_, m) => shell.Dispatcher.Dispatch(() => RefreshPending(m.Count)));
        RefreshPending();

        var name = new Label { Text = "Slim and Shape", FontFamily = "BarlowBold", FontSize = 16, VerticalOptions = LayoutOptions.Center };
        Ink(name);
        var role = new Label { Text = roleName, FontSize = 12, VerticalOptions = LayoutOptions.Center, Opacity = 0.7 };
        Ink(role);
        var head = new HorizontalStackLayout { Spacing = 10, Padding = new Thickness(16, 0), Children = { new LogoMark { Size = 36 }, new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { name, role } } } };
        ToolTipProperties.SetText(head, "Slim and Shape, " + roleName);
        SemanticProperties.SetDescription(head, "Slim and Shape, " + roleName);

        var me = Picture(shell, header, items);
        me.HorizontalOptions = LayoutOptions.Start;
        me.Margin = new Thickness(22, 6, 0, 0);

        shell.Navigated += (_, _) =>
        {
            ApplyTab();
            if (lastItem != shell.CurrentItem) lastItem = null;   // left by another way: the old tab no longer says where you are
            // open the fold that holds the page you landed on (an overview link, a Home tile)
            foreach (var (_, _, kids) in folds)
                if (links.Any(l => shell.CurrentItem == l.Item && kids.Children.Contains(l.Row))) kids.IsVisible = true;
        };
        Mount(shell, 240, head, body, new VerticalStackLayout { Spacing = 6, Children = { bottom, me } }, Refresh);
    }

    /// <summary>One menu row: icon and words, a fold chevron, an optional tag; a real Button on top for keyboard and screen readers.</summary>
    sealed class MenuRow : Grid
    {
        readonly Border _bg = new() { StrokeThickness = 0, StrokeShape = new RoundRectangle { CornerRadius = 10 }, InputTransparent = true };
        readonly IconView _icon;
        readonly Label _text;
        readonly HorizontalStackLayout _line;
        public Button Button { get; } = new() { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0, CornerRadius = 10 };

        public MenuRow(string icon, string label, bool child, bool chevron = false)
        {
            HeightRequest = child ? 36 : 40;
            _icon = new IconView { Icon = icon, Size = child ? 14 : 16, ChipSize = child ? 24 : 28, Bare = true, VerticalOptions = LayoutOptions.Center };
            _text = new Label { Text = label, FontFamily = child ? "BarlowMedium" : "BarlowSemiBold", FontSize = child ? 13 : 14, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation };
            Ink(_text);
            var grid = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8, Padding = new Thickness(child ? 28 : 8, 0, 8, 0), InputTransparent = true, CascadeInputTransparent = true };
            grid.Add(_icon, 0, 0);
            grid.Add(_text, 1, 0);
            _line = new HorizontalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.Center };
            if (chevron) _line.Children.Add(new IconView { Icon = "chevron-down", Size = 12, ChipSize = 16, Bare = true });
            grid.Add(_line, 2, 0);
            Children.Add(_bg);
            Children.Add(grid);
            Children.Add(Button);
            ToolTipProperties.SetText(Button, label);
            SemanticProperties.SetDescription(Button, label);
            Selected = false;
        }

        public void Trailing(View view) => _line.Children.Insert(0, view);

        public bool Selected
        {
            get => _icon.Selected;
            set
            {
                // the current page on a black (light) or yellow (dark) bar, as the strip's square
                _icon.Selected = value;
                if (value)
                {
                    _bg.SetAppThemeColor(BackgroundColorProperty, Theme("InkLight"), Theme("Signal"));
                    _text.SetAppThemeColor(Label.TextColorProperty, Theme("Signal"), Theme("Black"));
                }
                else
                {
                    _bg.BackgroundColor = Colors.Transparent;
                    Ink(_text);
                }
            }
        }
    }

    static Label Caption(string text, bool top)
    {
        var label = new Label { Text = text.ToUpperInvariant(), FontFamily = "BarlowSemiBold", FontSize = 11, CharacterSpacing = 0.6, Opacity = 0.65, Margin = new Thickness(8, top ? 14 : 2, 8, 4) };
        Ink(label);
        SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level2);
        return label;
    }

    // the strip's ink: near-black on yellow (light), near-white on near-black (dark)
    static void Ink(Label label) => label.SetAppThemeColor(Label.TextColorProperty, Theme("InkLight"), Theme("InkDark"));

    static ContentView Logo(string roleName)
    {
        var logo = new ContentView { Content = new LogoMark { Size = 44 } };
        ToolTipProperties.SetText(logo, "Slim and Shape, " + roleName);
        SemanticProperties.SetDescription(logo, "Slim and Shape, " + roleName);
        return logo;
    }

    // your picture opens Settings, as the header picture does on phones
    static Avatar Picture(Shell shell, HeaderViewModel header, List<ShellItem> items)
    {
        var avatar = new Avatar { Diameter = 36, BindingContext = header };
        avatar.SetBinding(Avatar.LettersProperty, static (HeaderViewModel vm) => vm.Letters);
        avatar.SetBinding(Avatar.UrlProperty, static (HeaderViewModel vm) => vm.AvatarUrl);
        var settings = items.FirstOrDefault(i => i.Route == "settings");
        avatar.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => { if (settings is not null) shell.CurrentItem = settings; }) });
        ToolTipProperties.SetText(avatar, "Settings");
        SemanticProperties.SetDescription(avatar, "Your profile picture. Opens Settings.");
        return avatar;
    }

    static void Mount(Shell shell, double width, View top, View middle, View bottom, Action refresh)
    {
        var layout = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], RowSpacing = 12 };
        layout.Add(top, 0, 0);
        layout.Add(new ScrollView { Content = middle, VerticalScrollBarVisibility = ScrollBarVisibility.Never }, 0, 1);
        layout.Add(bottom, 0, 2);

        // Phase 9 (D23, approved mockup): a full-height strip flush with the window's left edge, yellow (light) or near-black (dark)
        var rail = new Border { StrokeThickness = 0, Padding = new Thickness(0, 14), WidthRequest = width, Content = layout };
        rail.SetAppThemeColor(VisualElement.BackgroundColorProperty, Theme("RailLight"), Theme("RailDark"));

        shell.FlyoutHeader = null;
        shell.FlyoutFooter = null;
        shell.FlyoutWidth = width;   // FlyoutWidth = 72 for the icon strip
        shell.SetAppThemeColor(Shell.FlyoutBackgroundColorProperty, Theme("BackdropLight"), Theme("BackdropDark"));
        shell.FlyoutContent = rail;
        shell.Navigated += (_, _) => refresh();
        refresh();
    }

    // a 48 x 48 click target (real Button: keyboard, screen reader) with the icon drawn on top; the page name shows on hover
    static (Grid Cell, Button Button, IconView Icon) IconCell(string icon, string name)
    {
        var button = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0, WidthRequest = 48, HeightRequest = 48, CornerRadius = 14 };
        ToolTipProperties.SetText(button, name);
        SemanticProperties.SetDescription(button, name);
        var view = new IconView { Icon = icon, Size = 18, ChipSize = 40, Bare = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        return (new Grid { WidthRequest = 48, HeightRequest = 48, HorizontalOptions = LayoutOptions.Center, Children = { button, view } }, button, view);
    }

    static BoxView Divider()
    {
        var line = new BoxView { HeightRequest = 1.5, WidthRequest = 28, HorizontalOptions = LayoutOptions.Center, Margin = new Thickness(0, 4) };
        line.SetAppThemeColor(BoxView.ColorProperty, Color.FromRgba(0x11, 0x11, 0x11, 0.3), Color.FromRgba(0xF2, 0xF2, 0xF2, 0.2));   // the strip's ink, faint
        return line;
    }

    static Color Theme(string key) => (Color)Application.Current!.Resources[key];
}
#endif
