using System.Xml.Linq;

public class WindowsAdminNavigationTests
{
    [Fact]
    public void Grouping_is_Windows_only_and_admin_only()
    {
        var setup = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "StaffShell.xaml.cs")).Replace("\r\n", "\n");
        // D15: the sub-admin gets the overview and the grouped menu too; everyone else gets the plain icon rail
        Assert.Contains("#if WINDOWS\n\t\tif (role is AppRole.Admin or AppRole.SubAdmin) SetupAdminOverview(sp, role == AppRole.Admin);\n"
            + "\t\tControls.IconRail.Install(this, RoleName(role), (Core.ViewModels.HeaderViewModel)GetTitleView(this).BindingContext, groupFrontDesk: role is AppRole.Admin or AppRole.SubAdmin);\n#endif", setup);
        Assert.False(File.Exists(Path.Combine(TestPaths.AppFolder(), "StaffShell.Windows.cs")));   // the old text menu is gone
    }

    [Fact]
    public void Overview_is_a_Windows_only_landing_item_and_shortcuts_match_existing_admin_pages()
    {
        var app = TestPaths.AppFolder();
        var setup = File.ReadAllText(Path.Combine(app, "StaffShell.Overview.Windows.cs"));
        Assert.StartsWith("#if WINDOWS", setup);
        Assert.EndsWith("#endif", setup.TrimEnd());
        Assert.Contains("Items.Insert(0, overview)", setup);
        Assert.Contains("CurrentItem = overview", setup);
        Assert.Contains("new AdminOverviewPage(services.GetRequiredService<Core.ViewModels.AdminOverviewViewModel>(), superAdmin)", setup);
        var view = File.ReadAllText(Path.Combine(app, "Views", "AdminOverviewPage.Windows.cs"));
        Assert.StartsWith("#if WINDOWS", view);
        Assert.EndsWith("#endif", view.TrimEnd());
        Assert.DoesNotContain("HomeViewModel", view);
        var shared = XDocument.Load(Path.Combine(app, "StaffShell.xaml")).Root!;
        var titles = shared.Elements().Where(e => (string?)e.Attribute("ClassId") is "admin" or "super").Select(e => (string?)e.Attribute("Title")).ToArray();
        var vm = new GymMembership.Core.ViewModels.AdminOverviewViewModel(null!, null!);
        Assert.Equal(titles.Order(), vm.Cards.Select(c => c.Title).Order());
    }

    [Fact]
    public void Overview_card_actions_are_in_a_bottom_row_not_in_the_variable_height_text_stack()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Views", "AdminOverviewPage.Windows.cs"));
        Assert.Contains("new RowDefinition(GridLength.Star)", source);
        Assert.Contains("new RowDefinition(GridLength.Auto)", source);
        Assert.Contains("cardLayout.SetRow(actions, 1)", source);
        Assert.Contains("Content = cardLayout", source);
        Assert.Contains("actions.Children.Add(open)", source);
        Assert.DoesNotContain("content.Children.Add(open)", source);
        Assert.DoesNotContain("content.Children.Add(retry)", source);
    }

    [Fact]
    public void All_existing_pages_and_routes_remain_in_the_shared_shell()
    {
        var root = XDocument.Load(Path.Combine(TestPaths.AppFolder(), "StaffShell.xaml")).Root!;
        var items = root.Elements().Where(e => e.Name.LocalName == "FlyoutItem").ToList();
        Assert.Equal(15, items.Count);
        Assert.Equal(new[] { "home", "verify", "cashier", "checkin", "amenities" },
            items.Where(e => (string?)e.Attribute("ClassId") == "desk").Select(e => (string?)e.Attribute("Route")));
        Assert.Equal(4, items.Count(e => (string?)e.Attribute("ClassId") == "admin"));   // D15: shared with the sub-admin
        Assert.Equal(4, items.Count(e => (string?)e.Attribute("ClassId") == "super"));   // super admin only
        Assert.Single(items, e => (string?)e.Attribute("Route") == "messages");
        Assert.Single(items, e => (string?)e.Attribute("Route") == "settings");
    }
}
