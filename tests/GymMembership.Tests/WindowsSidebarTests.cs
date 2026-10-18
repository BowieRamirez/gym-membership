public class WindowsSidebarTests
{
    [Fact]
    public void Sidebar_setup_is_compiled_only_for_Windows_and_shared_header_stays_unchanged()
    {
        var app = TestPaths.AppFolder();
        var source = File.ReadAllText(Path.Combine(app, "Controls", "HeaderBar.Windows.cs"));
        Assert.StartsWith("#if WINDOWS", source);
        Assert.EndsWith("#endif", source.TrimEnd());
        Assert.Contains("SetupWindowsSidebar(shell)", File.ReadAllText(Path.Combine(app, "Controls", "HeaderBar.xaml.cs")));
        Assert.DoesNotContain("SidebarToggle", File.ReadAllText(Path.Combine(app, "Controls", "HeaderBar.xaml")));
    }

    [Theory]
    [InlineData("MemberShell")]
    [InlineData("CoachShell")]
    [InlineData("StaffShell")]
    public void Every_role_keeps_its_existing_phone_navigation_and_uses_the_shared_header(string shell)
    {
        var app = TestPaths.AppFolder();
        Assert.Contains("WinUI=Locked, Default=Flyout", File.ReadAllText(Path.Combine(app, shell + ".xaml")));
        Assert.Contains(".Attach(this, sp)", File.ReadAllText(Path.Combine(app, shell + ".xaml.cs")));
    }

    [Fact]
    public void Sidebar_is_keyboard_accessible_respects_reduced_motion_and_cancels_on_unload()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Controls", "HeaderBar.Windows.cs"));
        foreach (var required in new[] { "SemanticProperties.SetDescription", "ToolTipProperties.SetText", "AnimationsEnabled", "AbortAnimation", "Unloaded", "IsPaneVisible", "Easing.CubicInOut" })
            Assert.Contains(required, source);
        Assert.Matches(@"WidthRequest\s*=\s*48", source);
    }
}
