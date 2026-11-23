using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// A {StaticResource X} that is neither app-wide nor on its own page only fails when the page opens, and on Windows that closes the app
/// (2026-11-23: Amenity status used Packages' page-only "Any").
/// </summary>
public class StaticResourceTests
{
    [Fact]
    public void Every_static_resource_a_page_uses_is_defined_app_wide_or_on_that_page()
    {
        var app = TestPaths.AppFolder();
        var xamls = Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToArray();
        static IEnumerable<string> Keys(string xaml) => Regex.Matches(xaml, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        var shared = new HashSet<string>(new[] { "App.xaml", Path.Combine("Resources", "Styles", "Colors.xaml"), Path.Combine("Resources", "Styles", "Styles.xaml") }
            .SelectMany(f => Keys(File.ReadAllText(Path.Combine(app, f)))));

        var missing = xamls
            .Where(p => !p.Contains(Path.Combine("Resources", "Styles")) && Path.GetFileName(p) != "App.xaml")
            .SelectMany(p =>
            {
                var xaml = File.ReadAllText(p);
                var local = Keys(xaml).ToHashSet();
                return Regex.Matches(xaml, @"StaticResource\s+([\w.]+)").Select(m => m.Groups[1].Value)
                    .Where(k => !shared.Contains(k) && !local.Contains(k))
                    .Select(k => $"{Path.GetRelativePath(app, p)}: {k}");
            })
            .ToList();
        Assert.Empty(missing);
    }
}
