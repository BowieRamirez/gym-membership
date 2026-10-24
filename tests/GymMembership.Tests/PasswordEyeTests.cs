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
                var name = Regex.Match(entry.Value, @"x:Name=""(\w+)""");
                Assert.True(name.Success, $"{page}: a password box has no x:Name for its eye");
                Assert.Matches($@"<c:EyeButton\b[^>]*Entry=""\{{x:Reference {name.Groups[1].Value}\}}""", xaml);
            }
        Assert.Equal(10, boxes);   // Login 3, Register 2, Settings 3, Users 2
    }

    [Fact]
    public void The_eye_icons_are_drawn()
    {
        var icons = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Controls", "Icons.cs"));
        Assert.Contains("[\"eye\"]", icons);
        Assert.Contains("[\"eye-off\"]", icons);
    }
}
