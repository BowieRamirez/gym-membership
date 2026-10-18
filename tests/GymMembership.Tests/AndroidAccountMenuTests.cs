using System.Xml.Linq;

public class AndroidAccountMenuTests
{
    static string Read(string path) => File.ReadAllText(Path.Combine(TestPaths.AppFolder(), path)).Replace("\r\n", "\n");

    [Fact]
    public void Dropdown_is_Android_only_and_scoped_to_member_and_coach_shells()
    {
        var source = Read("Controls/HeaderBar.Android.cs");
        Assert.StartsWith("#if ANDROID", source);
        Assert.EndsWith("#endif", source.TrimEnd());
        Assert.Contains("if (shell is not (MemberShell or CoachShell)) return;", source);
        Assert.Contains("#if ANDROID\n\t\tSetupAndroidAccountMenu(shell);\n#endif", Read("Controls/HeaderBar.xaml.cs"));
    }

    [Fact]
    public void Existing_signout_binding_remains_the_default_for_other_platforms_and_roles()
    {
        var xaml = XDocument.Parse(Read("Controls/HeaderBar.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var button = xaml.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "SignOutButton");
        Assert.Equal("Sign out", (string?)button.Attribute("Text"));
        Assert.Equal("{Binding SignOutCommand}", (string?)button.Attribute("Command"));
        Assert.Equal("{OnPlatform WinUI=False, Default=True}", (string?)button.Attribute("IsVisible"));
        Assert.Contains("SetupWindowsSidebar(shell)", Read("Controls/HeaderBar.xaml.cs"));
    }

    [Fact]
    public void Dropdown_is_anchored_accessible_and_reuses_settings_and_signout_actions()
    {
        var source = Read("Controls/HeaderBar.Android.cs");
        Assert.Contains("SignOutButton.RemoveBinding(Button.CommandProperty)", source);
        Assert.Contains("SignOutButton.Text = \"⌄\"", source);
        Assert.Contains("SignOutButton.WidthRequest = 48", source);
        Assert.Contains("SemanticProperties.SetDescription", source);
        Assert.Contains("new PopupMenu(anchor.Context!, anchor, GravityFlags.End)", source);
        Assert.Contains("items.Add(0, 1, 0, \"Settings\")", source);
        Assert.Contains("items.Add(0, 2, 1, \"Sign out\")", source);
        Assert.Contains("shell.CurrentPage is not SettingsPage", source);
        Assert.Contains("shell.GoToAsync(\"header-settings\")", source);
        Assert.Contains("vm.SignOutCommand.ExecuteAsync(null)", source);
        Assert.Contains("_accountActionRunning", source);
        Assert.Contains("shell.Unloaded += (_, _) => _accountMenu?.Dismiss()", source);
    }

    [Fact]
    public void Android_hides_settings_only_from_more_not_from_programmatic_routes()
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("#if ANDROID\n        moreItems = moreItems.Where(item => item.Title != \"Settings\");\n#endif", source);
        Assert.Contains("new PhoneMorePage(moreItems.Select", source);
        Assert.Contains("[\"Settings\"] = typeof(SettingsPage)", source);
        Assert.Contains("_secondary.Add(route, source)", source);
    }
}
