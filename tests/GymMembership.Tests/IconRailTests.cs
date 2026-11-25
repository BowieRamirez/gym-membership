using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

/// <summary>The Windows side menu is a slim strip of icons: every page needs a drawn icon and a hover name, and nothing may get lost on a short window.</summary>
public class IconRailTests
{
    static string AppFile(params string[] parts) => Path.Combine(new[] { TestPaths.AppFolder() }.Concat(parts).ToArray());
    static string Rail() => File.ReadAllText(AppFile("Controls", "IconRail.Windows.cs"));

    static Dictionary<string, string> PageIcons() =>
        Regex.Matches(Rail(), "\\[\"([^\"]+)\"\\]\\s*=\\s*\"([a-z-]+)\"").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    [Theory]
    [InlineData("MemberShell")]
    [InlineData("CoachShell")]
    [InlineData("StaffShell")]
    public void Every_page_in_every_shell_has_a_drawn_icon(string shell)
    {
        var titles = XDocument.Load(AppFile(shell + ".xaml")).Root!.Elements().Where(e => e.Name.LocalName == "FlyoutItem")
            .Select(e => WebUtility.HtmlDecode((string)e.Attribute("Title")!)).Append("Overview");
        var icons = PageIcons();
        var drawn = IconTests.Drawn();
        Assert.All(titles, t => Assert.True(icons.ContainsKey(t), $"no rail icon for '{t}'"));
        Assert.All(icons.Values.Concat(["briefcase", "log-out"]), i => Assert.Contains(i, drawn));
    }

    [Fact]
    public void Rail_is_Windows_only_scrolls_and_names_each_icon_on_hover()
    {
        var source = Rail();
        Assert.StartsWith("#if WINDOWS", source);
        Assert.EndsWith("#endif", source.TrimEnd());
        foreach (var required in new[] { "new ScrollView", "ToolTipProperties.SetText", "SemanticProperties.SetDescription", "Mount(shell, 72,", "CurrentItem = item", "Navigated" })
            Assert.Contains(required, source);
        // custom content navigates through the original Shell items; it never replaces them or forces the native selection
        foreach (var banned in new[] { "Items.Remove", "new ShellContent", "MenuItemsSource", "SelectedItem" })
            Assert.DoesNotContain(banned, source);
    }

    // scripts/Test-WindowsAdminNavigation.ps1 drives the menu by these ids
    [Fact]
    public void Rail_keeps_the_ids_the_windows_ui_script_uses()
    {
        var source = Rail();
        Assert.Contains("\"AdminNavigation-\" + item.Route", source);
        Assert.Contains("\"AdminFrontDeskGroup\"", source);
        Assert.Contains(", expanded. Collapse group", source);
        Assert.Contains(", collapsed. Expand group", source);
        Assert.Contains("\", current page\"", source);
    }

    // Fix 4 (W1): the System admin and Admin get the strip widened with words; folds start closed and stand in for a hidden current page
    [Fact]
    public void Admin_menu_has_words_folds_and_the_Pending_tag()
    {
        var source = Rail();
        Assert.Contains("Mount(shell, 240,", source);
        Assert.Contains("AdminMenu.Sections", source);
        Assert.Contains("IsVisible = false", source);
        Assert.Contains("inside.Row is not null && !kids.IsVisible", source);
        Assert.Contains("\", current page: \"", source);
        Assert.Contains("Register<AttendancePendingChanged>", source);
        var shell = File.ReadAllText(AppFile("StaffShell.xaml.cs"));
        Assert.Contains("IconRail.InstallMenu(", shell);
    }

    [Fact]
    public void Dividers_only_between_runs_that_have_icons()
    {
        var source = Rail();
        Assert.Contains("if (run.Count == 0) return;", source);
        Assert.Contains("if (pages.Children.Count > 0) pages.Children.Add(Divider());", source);
    }

    [Theory]
    [InlineData("MemberShell")]
    [InlineData("CoachShell")]
    [InlineData("StaffShell")]
    public void All_shells_install_the_rail_on_Windows(string shell)
    {
        var source = File.ReadAllText(AppFile(shell + ".xaml.cs")).Replace("\r\n", "\n");
        var at = source.IndexOf("Controls.IconRail.Install(", StringComparison.Ordinal);
        Assert.True(at > 0, "rail not installed");
        Assert.True(source.LastIndexOf("#if WINDOWS", at, StringComparison.Ordinal) > source.LastIndexOf("#endif", at, StringComparison.Ordinal), "install outside #if WINDOWS");
    }

    [Fact]
    public void Windows_header_leaves_the_picture_and_sign_out_to_the_rail()
    {
        var header = File.ReadAllText(AppFile("Controls", "HeaderBar.xaml.cs"));
        Assert.DoesNotContain("FlyoutFooter", header);
        Assert.Contains("IsVisible=\"{OnPlatform WinUI=False, Default=True}\"", File.ReadAllText(AppFile("Controls", "HeaderBar.xaml")).Split("<c:Avatar")[1].Split(">")[0]);
    }
}
