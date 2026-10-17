using System.Xml.Linq;

public class WindowsAdminNavigationTests
{
    [Fact]
    public void Grouping_is_Windows_only_and_admin_only()
    {
        var app = TestPaths.AppFolder();
        var setup = File.ReadAllText(Path.Combine(app, "StaffShell.xaml.cs")).Replace("\r\n", "\n");
        Assert.Contains("#if WINDOWS\n\t\tif (role == AppRole.Admin)\n\t\t{\n\t\t\tSetupAdminOverview(sp);\n\t\t\tSetupAdminNavigation();\n\t\t}\n#endif", setup);
        var source = File.ReadAllText(Path.Combine(app, "StaffShell.Windows.cs"));
        Assert.StartsWith("#if WINDOWS", source);
        Assert.EndsWith("#endif", source.TrimEnd());
    }

    [Fact]
    public void Operations_group_starts_collapsed_and_uses_supported_Shell_flyout_content()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "StaffShell.Windows.cs"));
        foreach (var required in new[] { "Front desk operations", "Administration", "IsVisible = false", "FlyoutContent =", "CurrentItem = item", "Navigated", "SemanticProperties.SetDescription" })
            Assert.Contains(required, source);
        Assert.DoesNotContain("Items.Remove", source);
        Assert.DoesNotContain("new ShellContent", source);
        Assert.Contains("item.ClassId == \"desk\" || item.Route == \"messages\"", source);
    }

    [Fact]
    public void Custom_sidebar_never_replaces_native_Shell_items_or_forces_native_selection()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "StaffShell.Windows.cs"));
        Assert.DoesNotContain("MenuItemsSource", source);
        Assert.DoesNotContain("SelectedItem", source);
        Assert.DoesNotContain("Microsoft.UI.Xaml.Controls.NavigationView", source);
    }

    [Fact]
    public void Navigation_button_content_is_left_aligned_without_shrinking_the_click_target()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "StaffShell.Windows.cs"));
        Assert.Contains("HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left", source);
        Assert.Contains("HorizontalOptions = LayoutOptions.Fill", source);
        Assert.Contains("new Thickness(16, 0, 0, 0)", source);
        var footer = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Controls", "SignOutFooter.xaml.cs"));
        Assert.Contains("#if WINDOWS", footer);
        Assert.Contains("HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left", footer);
    }

    [Fact]
    public void Section_headings_share_typography_and_do_not_change_weight_with_selection()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "StaffShell.Windows.cs"));
        Assert.Contains("toggle.FontFamily = \"BarlowSemiBold\"", source);
        Assert.Contains("FontFamily = toggle.FontFamily, FontSize = toggle.FontSize", source);
        Assert.Contains("administration.SetAppThemeColor(Label.TextColorProperty, ThemeColor(\"InkLight\"), ThemeColor(\"InkDark\"))", source);
        Assert.DoesNotContain("toggle.FontFamily = current.Length", source);
    }

    [Fact]
    public void Sidebar_has_uniform_row_spacing_and_section_headings_larger_than_page_links()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "StaffShell.Windows.cs"));
        Assert.Contains("toggle.FontSize = 18", source);
        Assert.Contains("FontFamily = \"Barlow\", FontSize = 16", source);
        Assert.Contains("Margin = new Thickness(12, 0, 0, 0)", source);
        Assert.DoesNotContain("new Thickness(12, 16, 0, 0)", source);
        Assert.DoesNotContain("button.Margin =", source);
        Assert.Contains("Spacing = 4", source);
    }

    [Fact]
    public void Administration_pages_match_the_operations_layout_without_a_collapse_action()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "StaffShell.Windows.cs"));
        Assert.Contains("var administrationPages = new VerticalStackLayout { Spacing = operations.Spacing, Padding = operations.Padding };", source);
        Assert.Contains("menu.Children.Add(administrationPages)", source);
        Assert.Contains("else if (item.ClassId == \"admin\")", source);
        Assert.Contains("administrationPages.Children.Add(button)", source);
        Assert.DoesNotContain("administrationPages.IsVisible", source);
        Assert.DoesNotContain("administration.Clicked", source);
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
        Assert.Contains("services.GetRequiredService<AdminOverviewPage>()", setup);
        var view = File.ReadAllText(Path.Combine(app, "Views", "AdminOverviewPage.Windows.cs"));
        Assert.StartsWith("#if WINDOWS", view);
        Assert.EndsWith("#endif", view.TrimEnd());
        Assert.DoesNotContain("HomeViewModel", view);
        var shared = XDocument.Load(Path.Combine(app, "StaffShell.xaml")).Root!;
        var titles = shared.Elements().Where(e => (string?)e.Attribute("ClassId") == "admin").Select(e => (string?)e.Attribute("Title")).ToArray();
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
        Assert.Equal(8, items.Count(e => (string?)e.Attribute("ClassId") == "admin"));
        Assert.Single(items, e => (string?)e.Attribute("Route") == "messages");
        Assert.Single(items, e => (string?)e.Attribute("Route") == "settings");
    }
}
