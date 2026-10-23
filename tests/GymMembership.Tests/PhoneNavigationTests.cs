using System.Xml.Linq;

public class PhoneNavigationTests
{
    static string Read(string path) => File.ReadAllText(Path.Combine(TestPaths.AppFolder(), path)).Replace("\r\n", "\n");

    [Fact]
    public void Bottom_bar_requires_a_mobile_platform_and_phone_idiom()
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("#if ANDROID || IOS", source);
        Assert.Contains("return DeviceInfo.Idiom == DeviceIdiom.Phone;", source);
        Assert.Contains("#else\n            return false;\n#endif", source);
        Assert.True(source.IndexOf("if (!IsSupported) return;") < source.IndexOf("new PhoneNavigation(shell, coach, services, access)"));
    }

    [Theory]
    [InlineData("MemberShell", "false")]
    [InlineData("CoachShell", "true")]
    public void Only_member_and_coach_shells_attach_phone_navigation_with_services(string shell, string coach)
    {
        Assert.Contains($"PhoneNavigation.Attach(this, coach: {coach}, services: sp", Read(shell + ".xaml.cs"));
        Assert.DoesNotContain("PhoneNavigation", Read("StaffShell.xaml.cs"));
        Assert.Contains("WinUI=Locked, Default=Flyout", Read(shell + ".xaml"));
    }

    [Fact]
    public void Membership_locks_are_preserved_instead_of_resolving_the_unrestricted_page()
    {
        var shell = Read("MemberShell.xaml.cs");
        Assert.True(shell.IndexOf("new LockedPage") < shell.IndexOf("PhoneNavigation.Attach"));
        Assert.Contains("services: sp, access: access", shell);
        Assert.Contains("Navigated += async (_, _) => await RecheckAsync()", shell);
        var navigation = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("ContentTemplate = source.ContentTemplate", navigation);
        Assert.Contains("if (item.ClassId != \"members-only\" || access?.Active != false)", navigation);
        Assert.Contains("_secondary.Add(route, source)", navigation);
    }

    [Fact]
    public void Phone_coach_opens_on_the_dashboard_and_keeps_requests_in_more_with_five_tabs()
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("(\"Dashboard\", \"Dashboard\", \"overview\"), (\"Sessions\", \"Sessions\", \"sessions\"), (\"Trainees\", \"Trainees\", \"trainees\"), (\"Messages\", \"Messages\", \"messages\")", source);
        Assert.Contains("<FlyoutItem Title=\"Dashboard\" Route=\"dashboard\"><ShellContent ContentTemplate=\"{DataTemplate views:AttendancePage}\" />", Read("CoachShell.xaml"));
        Assert.Contains("(\"My membership\", \"Membership\", \"membership\"), (\"Packages\", \"Packages\", \"packages\"), (\"Sessions\", \"Sessions\", \"sessions\"), (\"Messages\", \"Messages\", \"messages\")", source);
        Assert.Contains("_bar.Items.Add(_more)", source);
        Assert.Contains("new PhoneMorePage", source);
        Assert.Contains("FlyoutBehavior.Disabled", source);
        Assert.Contains("original.Where(item => !primary.Any(p => p.Item1 == item.Title))", source);
        Assert.Contains("original.Single(item => item.Title == sourceTitle)", source);
    }

    [Theory]
    [InlineData("Amenities", "AmenitiesPage")]
    [InlineData("Coaches", "CoachesPage")]
    [InlineData("Time requests", "RequestsPage")]
    [InlineData("My shifts", "ShiftsPage")]
    [InlineData("My coach profile", "CoachProfilePage")]
    [InlineData("Settings", "SettingsPage")]
    public void Every_more_destination_has_an_explicit_dependency_injected_page_factory(string title, string page)
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains($"[\"{title}\"] = typeof({page})", source);
        Assert.Contains("PhonePageTemplates.ForService(services, pageTypes[item.Title])", source);
    }

    [Fact]
    public void Mobile_bar_has_square_corners_and_keeps_theme_aware_backgrounds()
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("background.SetCornerRadius(0)", source);
        Assert.Contains("bar.Layer.CornerRadius = 0", source);
        Assert.DoesNotContain("SetCornerRadii", source);
        Assert.DoesNotContain("MaskedCorners", source);
        Assert.Contains("Shell.GetTabBarBackgroundColor(_shell)", source);
        Assert.Contains("_shell.CurrentPage?.BackgroundColor", source);
        Assert.Contains("bar.BackgroundTintList = null", source);
        Assert.Contains("parent.SetBackgroundColor(pageBackground.ToPlatform())", source);
    }

    [Theory]
    [InlineData("Services/AppServices.cs")]
    [InlineData("Views/LockedPage.cs")]
    [InlineData("Views/MyMembershipPage.xaml.cs")]
    public void Existing_absolute_navigation_uses_phone_aware_routing(string path)
    {
        Assert.Contains("PhoneNavigation.GoToAsync(Shell.Current,", Read(path));
        Assert.DoesNotContain("await Shell.Current.GoToAsync(", Read(path));
        Assert.Contains("return shell.GoToAsync(route)", Read("Controls/PhoneNavigation.cs"));
    }

    [Fact]
    public void More_preserves_native_back_navigation_and_prevents_double_taps()
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("if (_opening ||", source);
        Assert.Contains("finally { _opening = false; }", source);
        Assert.Contains("Navigation.PushAsync(page)", source);
        Assert.Contains("Navigation.PopToRootAsync(false)", source);
        Assert.Contains("MinimumHeightRequest = 56", Read("Views/PhoneMorePage.cs"));
        Assert.Contains("new ScrollView", Read("Views/PhoneMorePage.cs"));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("packages")]
    [InlineData("sessions")]
    [InlineData("requests")]
    [InlineData("trainees")]
    [InlineData("messages")]
    [InlineData("more")]
    [InlineData("overview")]
    public void Every_phone_tab_has_a_valid_24_unit_vector_icon(string name)
    {
        var svg = XDocument.Parse(Read($"Resources/Images/phone_{name}.svg"));
        Assert.Equal("0 0 24 24", svg.Root!.Attribute("viewBox")!.Value);
        Assert.NotEmpty(svg.Root.Elements());
    }
}
