using Xunit;

/// <summary>
/// Phase 9 (D24, D25): the HF monogram replaces the dumbbell everywhere small (side menu, phone headers, app icon, splash), the badge
/// ships as an app image for the sign-in pages, and the app is called "Gym Center", with "Health &amp; Fitness Gym Center" in full.
/// </summary>
public class LogoTests
{
    static string AppFile(params string[] parts) => Path.Combine(new[] { TestPaths.AppFolder() }.Concat(parts).ToArray());

    [Fact]
    public void App_icon_and_splash_are_the_HF_monogram()
    {
        var foreground = File.ReadAllText(AppFile("Resources", "AppIcon", "appiconfg.svg"));
        Assert.Contains("#FFEC00", foreground);
        Assert.Contains("<circle", foreground);   // the ring
        Assert.DoesNotContain("#E4F58A", foreground);
        Assert.DoesNotContain("#ffffff", foreground, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#FFEC00", File.ReadAllText(AppFile("Resources", "Splash", "splash.svg")));
        var project = File.ReadAllText(AppFile("GymMembership.App.csproj"));
        Assert.DoesNotContain("#512BD4", project);
        Assert.Contains("ForegroundFile=\"Resources\\AppIcon\\appiconfg.svg\" Color=\"#111111\"", project);
        Assert.Contains("<MauiSplashScreen Include=\"Resources\\Splash\\splash.svg\" Color=\"#111111\"", project);
    }

    [Fact]
    public void The_monogram_replaces_the_dumbbell_everywhere()
    {
        Assert.False(File.Exists(AppFile("Controls", "DumbbellLogo.cs")));
        var app = TestPaths.AppFolder();
        foreach (var file in Directory.GetFiles(app, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".xaml") || f.EndsWith(".cs")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            Assert.DoesNotContain("DumbbellLogo", File.ReadAllText(file));
        foreach (var shell in new[] { "MemberShell.xaml", "CoachShell.xaml", "StaffShell.xaml" })
            Assert.Contains("<c:LogoMark", File.ReadAllText(AppFile(shell)));
        Assert.Contains("new LogoMark", File.ReadAllText(AppFile("Controls", "IconRail.Windows.cs")));
    }

    [Fact]
    public void Sign_in_and_create_account_show_the_brand()
    {
        // both pages sit in AuthShell, whose brand panel (desktop) or header (phone) carries the badge (9.4) or the monogram
        var styles = File.ReadAllText(AppFile("Resources", "Styles", "Styles.xaml"));
        var shell = styles[styles.IndexOf("x:Key=\"AuthShell\"")..];
        shell = shell[..shell.IndexOf("</ControlTemplate>")];
        Assert.True(shell.Contains("<c:LogoMark") || shell.Contains("gym_badge.png"));
        Assert.Contains("Health &amp; Fitness Gym Center", shell);
        Assert.Contains("ControlTemplate=\"{StaticResource AuthShell}\"", File.ReadAllText(AppFile("Views", "LoginPage.xaml")));
        Assert.Contains("ControlTemplate=\"{StaticResource AuthShell}\"", File.ReadAllText(AppFile("Views", "RegisterPage.xaml")));
        Assert.True(File.Exists(AppFile("Resources", "Images", "gym_badge.png")));
    }

    [Fact]
    public void The_app_is_called_Gym_Center()
    {
        Assert.Contains("<ApplicationTitle>Gym Center</ApplicationTitle>", File.ReadAllText(AppFile("GymMembership.App.csproj")));
        Assert.Contains("<ApplicationId>com.slimshape.gymmembership</ApplicationId>", File.ReadAllText(AppFile("GymMembership.App.csproj")));   // same app on devices
        Assert.Contains("\"Gym Center\"", File.ReadAllText(AppFile("App.xaml.cs")));
        foreach (var file in new[] { "App.xaml.cs", "MemberShell.xaml", "CoachShell.xaml", "StaffShell.xaml", Path.Combine("Controls", "IconRail.Windows.cs"), Path.Combine("Resources", "Styles", "Styles.xaml") })
            Assert.DoesNotContain("Gym Membership", File.ReadAllText(AppFile(file)));
        var demo = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "..", "GymMembership.Core", "Demo", "DemoGateway.cs"));
        Assert.Contains("GymName = \"Health & Fitness Gym Center\"", demo);
    }
}
