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
        Assert.Contains("(\"Dashboard\", \"Dashboard\", \"overview\"), (\"Sessions\", \"Sessions\", \"sessions\"), (\"My shifts\", \"Shifts\", \"trainees\"), (\"Messages\", \"Messages\", \"messages\")", source);
        Assert.Contains("<FlyoutItem Title=\"Dashboard\" Route=\"dashboard\"><ShellContent ContentTemplate=\"{DataTemplate views:AttendancePage}\" />", Read("CoachShell.xaml"));
        Assert.Contains("MemberMenu.Tabs(access?.Active ?? true)", source);
        Assert.Contains("[\"Book an amenity\"] = (\"Book an amenity\", \"Book\", \"bookings\")", source);   // its tab keeps the //bookings route the calendar opens
        Assert.DoesNotContain("MemberCalendarPage", source);   // the calendar lives inside Sessions, not as its own phone page   // F16: member tabs follow the membership (MemberCalendarTests pins the order)
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
    [InlineData("Renew or change", "PackagesPage")]
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

    [Fact]
    public void Queued_bar_styling_skips_unloaded_or_replaced_shells_and_handles_missing_colors()
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("bool _isLoaded;", source);
        Assert.Contains("_isLoaded = true;", source);
        Assert.Contains("_isLoaded = false;", source);
        var guard = source.IndexOf("if (!_isLoaded || !ReferenceEquals(_shell.Window?.Page, _shell)) return;");
        var traversal = source.IndexOf("void Visit(Android.Views.View view)");
        Assert.True(guard >= 0 && guard < traversal);
        Assert.Contains("Shell.GetTabBarBackgroundColor(_shell) ??", source);
        Assert.Contains("_shell.CurrentPage?.BackgroundColor ?? _shell.BackgroundColor ?? surface", source);
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

    // Back on a tab's own page finished the app's only activity; reopening built a new window, which always starts on sign-in.
    // The Shell still goes back when it can; when it can't, the app goes to the background, as Android does for other apps.
    [Theory]
    [InlineData("MemberShell.xaml.cs")]
    [InlineData("CoachShell.xaml.cs")]
    public void Back_on_a_main_page_sends_the_app_to_the_background_instead_of_closing_it(string shell)
    {
        Assert.Contains("protected override bool OnBackButtonPressed() => base.OnBackButtonPressed() || Controls.PhoneNavigation.SendToBackground();", Read(shell));
        Assert.Contains("MoveTaskToBack(true)", Read("Controls/PhoneNavigation.cs"));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("bookings")]
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

    // Phase 9 (D23): near-black in dark; owner 2026-11-12 (mockup phone-light-yellow.html, B): yellow in light. The selected tab is a
    // black pill with a yellow icon in light and a yellow pill with a black icon in dark.
    [Fact]
    public void Bottom_bar_is_yellow_and_black_with_the_selected_tab_on_a_pill()
    {
        var source = Read("Controls/PhoneNavigation.cs");
        Assert.Contains("TabBarBackgroundColorProperty, Color.FromArgb(\"#FFEC00\"), Color.FromArgb(\"#161616\")", source);
        Assert.Contains("TabBarForegroundColorProperty, Color.FromArgb(\"#FFEC00\"), Color.FromArgb(\"#111111\")", source);
        Assert.Contains("TabBarTitleColorProperty, Color.FromArgb(\"#111111\"), Color.FromArgb(\"#F2F2F2\")", source);
        Assert.Contains("TabBarUnselectedColorProperty, Color.FromArgb(\"#3D3A00\"), Color.FromArgb(\"#A6A6A6\")", source);
        Assert.Contains("ItemActiveIndicatorColor", source);
        // the app's MaterialComponents theme gives the indicator no shape or size, so without these it never draws
        Assert.Contains("ItemActiveIndicatorShapeAppearance", source);
        Assert.Contains("ItemActiveIndicatorWidth", source);
        Assert.Contains("ItemActiveIndicatorHeight", source);
        Assert.Contains("ItemActiveIndicatorColor = Android.Content.Res.ColorStateList.ValueOf(Color.FromArgb(dark ? \"#FFEC00\" : \"#111111\")", source);
        // the front desk's phone bar comes from the shared Shell style (layout unchanged, D22): the same colours, no pill
        var styles = Read("Resources/Styles/Styles.xaml");
        Assert.Contains("<Setter Property=\"Shell.TabBarBackgroundColor\" Value=\"{AppThemeBinding Light={StaticResource Signal}, Dark={StaticResource RailDark}}\" />", styles);
        Assert.Contains("<Setter Property=\"Shell.TabBarForegroundColor\" Value=\"{AppThemeBinding Light={StaticResource InkLight}, Dark={StaticResource Signal}}\" />", styles);
        Assert.Contains("<Setter Property=\"Shell.TabBarTitleColor\" Value=\"{AppThemeBinding Light={StaticResource InkLight}, Dark={StaticResource InkDark}}\" />", styles);
        foreach (var old in new[] { "#24282C", "#1F2226", "#ECEEF0", "#5C646C", "#9AA3AB", "#E4F58A", "#2C302B", "#EEF1EC", "#5A6159", "#A3AAA1" })
        {
            Assert.DoesNotContain(old, source);
            Assert.DoesNotContain(old, Read("Views/PhoneMorePage.cs"));
        }
    }

    [Fact]
    public void Phone_sessions_opens_the_member_calendar_inline_from_a_button()
    {
        var xaml = Read("Views/SessionsPage.xaml");
        Assert.Contains("x:Name=\"CalendarToggle\"", xaml);
        Assert.Contains("CalendarSection.IsVisible = !CalendarSection.IsVisible", Read("Views/SessionsPage.xaml.cs"));
        Assert.DoesNotContain("MemberCalendarPage", Read("MemberShell.xaml"));
    }

    [Fact]
    public void A_coach_has_settings_named_and_drawn_like_everyone_elses()
    {
        // owner, 2026-11-11: "Settings", not "Profile & settings"; on phones only in the account menu (More drops Settings), the same icon on Windows
        Assert.Contains("<FlyoutItem Title=\"Settings\" Route=\"settings\"><ShellContent ContentTemplate=\"{DataTemplate views:CoachProfilePage}\" />", Read("CoachShell.xaml"));
        Assert.Contains("moreItems.Where(item => item.Title != \"Settings\")", Read("Controls/PhoneNavigation.cs"));
        Assert.Contains("[\"Settings\"] = coach ? typeof(CoachProfilePage) : typeof(SettingsPage)", Read("Controls/PhoneNavigation.cs"));
        Assert.DoesNotContain("Profile & settings", Read("Controls/IconRail.Windows.cs"));
        Assert.Contains("Title=\"Settings\"", Read("Views/CoachProfilePage.xaml"));
    }
}
