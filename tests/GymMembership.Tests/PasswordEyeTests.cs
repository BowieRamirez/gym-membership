using System.Text.RegularExpressions;
using Xunit;

// Phase 12: every password box has its own eye button to show or hide what was typed.
public class PasswordEyeTests
{
    static IEnumerable<(string Page, string Xaml)> Pages() =>
        Directory.GetFiles(Path.Combine(TestPaths.AppFolder(), "Views"), "*.xaml").Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    [Fact]
    public void Every_password_box_has_an_eye_pointing_at_it()
    {
        var boxes = 0;
        foreach (var (page, xaml) in Pages())
            foreach (Match entry in Regex.Matches(xaml, @"<Entry\b[^>]*IsPassword=""True""[^>]*>"))
            {
                boxes++;
                // F8: the one exception. The member types their desk PIN on the front desk's screen; an eye would let staff read it
                if (page == "CheckInPage.xaml" && entry.Value.Contains(@"AutomationId=""DeskPin""")) continue;
                var name = Regex.Match(entry.Value, @"x:Name=""(\w+)""");
                Assert.True(name.Success, $"{page}: a password box has no x:Name for its eye");
                Assert.Matches($@"<c:EyeButton\b[^>]*Entry=""\{{x:Reference {name.Groups[1].Value}\}}""", xaml);
            }
        Assert.Equal(13, boxes);   // Login 3, Register 2, Settings 3 + desk PIN 2, Users 2, Check-in desk PIN 1
    }

    [Fact]
    public void The_eye_icons_are_drawn()
    {
        var icons = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Controls", "Icons.cs"));
        Assert.Contains("[\"eye\"]", icons);
        Assert.Contains("[\"eye-off\"]", icons);
    }
}
