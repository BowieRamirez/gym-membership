using System.Text.RegularExpressions;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>The line icons are drawn by name; a name nobody drew would show as a blank space, and an emoji would clash with the design.</summary>
public class IconTests
{
    static string AppFile(params string[] parts) => Path.Combine(new[] { TestPaths.AppFolder() }.Concat(parts).ToArray());

    static HashSet<string> Drawn() =>
        Regex.Matches(File.ReadAllText(AppFile("Controls", "Icons.cs")), "\\[\"([a-z-]+)\"\\]\\s*=").Select(m => m.Groups[1].Value).ToHashSet();

    static readonly string[] Pages = ["HomePage.xaml", "CashierPage.xaml"];

    [Fact]
    public void Every_icon_the_home_tiles_name_is_drawn()
    {
        var tiles = HomeViewModel.Build(new DeskSummary(0, 0, 0, 0, 0, 0, 0));
        Assert.All(tiles, t => Assert.Contains(t.Icon, Drawn()));
    }

    [Fact]
    public void Every_icon_a_page_names_is_drawn()
    {
        var drawn = Drawn();
        var named = Pages.SelectMany(p => Regex.Matches(File.ReadAllText(AppFile("Views", p)), "\\bIcon=\"([a-z-]+)\"").Select(m => m.Groups[1].Value)).ToList();
        Assert.NotEmpty(named);
        Assert.All(named, n => Assert.Contains(n, drawn));
    }

    [Fact]
    public void No_icon_is_an_emoji()
    {
        Assert.All(HomeViewModel.Build(new DeskSummary(0, 0, 0, 0, 0, 0, 0)), t => Assert.Matches("^[a-z]+(-[a-z]+)*$", t.Icon));
        foreach (var page in Pages)
            Assert.DoesNotContain(File.ReadAllText(AppFile("Views", page)), c => char.IsSurrogate(c));   // emoji outside the basic plane
    }

    [Fact]
    public void Each_drawn_icon_is_a_real_path_of_numbers_and_commands()
    {
        var source = File.ReadAllText(AppFile("Controls", "Icons.cs"));
        var paths = Regex.Matches(source, "\\[\"[a-z-]+\"\\]\\s*=\\s*\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(Drawn().Count, paths.Count);
        Assert.All(paths, p => Assert.Matches("^[MmLlHhVvCcSsQqTtAaZz0-9.\\- ]+$", p));
        Assert.All(paths, p => Assert.Matches("^[Mm]", p));
    }
}
