using System.Xml.Linq;

public class WindowsCoachOverviewTests
{
    [Fact]
    public void Windows_coach_overview_remains_the_first_sidebar_page()
    {
        var app = TestPaths.AppFolder();
        var shell = File.ReadAllText(Path.Combine(app, "CoachShell.xaml.cs")).Replace("\r\n", "\n");
        Assert.Contains("#if WINDOWS\n\t\tSetupCoachOverview(sp);\n#endif", shell);
        var setup = File.ReadAllText(Path.Combine(app, "CoachShell.Overview.Windows.cs"));
        Assert.StartsWith("#if WINDOWS", setup);
        Assert.Contains("Items.Insert(0, overview)", setup);
        Assert.Contains("CurrentItem = overview", setup);
        Assert.Contains("services.GetRequiredService<CoachOverviewPage>()", setup);
        var xaml = XDocument.Load(Path.Combine(app, "CoachShell.xaml")).Root!;
        Assert.Equal("Sessions", (string?)xaml.Elements().First(e => e.Name.LocalName == "FlyoutItem").Attribute("Title"));
        Assert.DoesNotContain(xaml.Elements(), e => (string?)e.Attribute("Title") == "Overview");
    }

    [Fact]
    public void Summary_page_has_no_quick_access_or_navigation_actions()
    {
        var page = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Views", "CoachOverviewPage.Windows.cs"));
        Assert.StartsWith("#if WINDOWS || ANDROID", page);
        Assert.EndsWith("#endif", page.TrimEnd());
        Assert.Contains("Coach overview", page);
        Assert.Contains("CoachOverviewRefresh", page);
        Assert.DoesNotContain("Shell.Current", page);
        Assert.DoesNotContain("GoTo", page);
        Assert.DoesNotContain("Clicked +=", page);
        Assert.DoesNotContain("Quick access", page);
        Assert.Equal(1, page.Split("new Button").Length - 1);
    }
}
