using Xunit;

/// <summary>The app's own dumbbell logo replaces the .NET template icon and splash, and greets people on the sign-in pages.</summary>
public class LogoTests
{
    static string AppFile(params string[] parts) => Path.Combine(new[] { TestPaths.AppFolder() }.Concat(parts).ToArray());

    [Fact]
    public void App_icon_is_the_dumbbell_not_the_template()
    {
        var foreground = File.ReadAllText(AppFile("Resources", "AppIcon", "appiconfg.svg"));
        Assert.Contains("#E4F58A", foreground);
        Assert.DoesNotContain("#ffffff", foreground, StringComparison.OrdinalIgnoreCase);
        var project = File.ReadAllText(AppFile("GymMembership.App.csproj"));
        Assert.DoesNotContain("#512BD4", project);
        Assert.Contains("ForegroundFile=\"Resources\\AppIcon\\appiconfg.svg\" Color=\"#000000\"", project);
        Assert.Contains("<MauiSplashScreen Include=\"Resources\\Splash\\splash.svg\" Color=\"#000000\"", project);
    }

    [Fact]
    public void Sign_in_and_create_account_show_the_logo()
    {
        // both pages sit in AuthShell, whose brand panel (desktop) or header (phone) carries the logo
        var styles = File.ReadAllText(AppFile("Resources", "Styles", "Styles.xaml"));
        var shell = styles[styles.IndexOf("x:Key=\"AuthShell\"")..];
        Assert.Contains("<c:DumbbellLogo", shell[..shell.IndexOf("</ControlTemplate>")]);
        Assert.Contains("ControlTemplate=\"{StaticResource AuthShell}\"", File.ReadAllText(AppFile("Views", "LoginPage.xaml")));
        Assert.Contains("ControlTemplate=\"{StaticResource AuthShell}\"", File.ReadAllText(AppFile("Views", "RegisterPage.xaml")));
    }
}
