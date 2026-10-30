using System.Text.RegularExpressions;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Microsoft.Maui.Graphics;
using Xunit;
using MauiShapes = Microsoft.Maui.Controls.Shapes;

/// <summary>The line icons are drawn by name; a name nobody drew would show as a blank space, and an emoji would clash with the design.</summary>
public class IconTests
{
    static string AppFile(params string[] parts) => Path.Combine(new[] { TestPaths.AppFolder() }.Concat(parts).ToArray());

    internal static HashSet<string> Drawn() =>
        Regex.Matches(File.ReadAllText(AppFile("Controls", "Icons.cs")), "\\[\"([a-z-]+)\"\\]\\s*=").Select(m => m.Groups[1].Value).ToHashSet();

    static readonly string[] Pages = ["HomePage.xaml", "CashierPage.xaml", "AttendancePage.xaml"];

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

    // Windows' graphics engine refuses a shape that holds NaN or infinity (error 0x88990011, "bad number") and the whole page crashes while it draws.
    // MAUI's own conversion made them out of shallow arcs, so this runs every icon through MAUI's real parser and scaling, as the app draws it.
    [Fact]
    public void Every_icon_scales_to_real_numbers_that_windows_can_draw()
    {
        var source = File.ReadAllText(AppFile("Controls", "Icons.cs"));
        var icons = Regex.Matches(source, "\\[\"([a-z-]+)\"\\]\\s*=\\s*\"([^\"]+)\"").Select(m => (Name: m.Groups[1].Value, Data: m.Groups[2].Value)).ToList();
        Assert.NotEmpty(icons);
        foreach (var (name, data) in icons)
        {
            var path = new MauiShapes.Path { Data = (MauiShapes.Geometry)new MauiShapes.PathGeometryConverter().ConvertFromInvariantString(data)!, Aspect = Stretch.Uniform, StrokeThickness = 2 };
            foreach (var size in new[] { 28.0, 22.0 })
            {
                var points = ((IShape)path).PathForBounds(new Rect(0, 0, size, size)).Points.ToList();
                Assert.NotEmpty(points);
                var bad = points.Count(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y));
                Assert.True(bad == 0, $"icon '{name}' has {bad} invalid numbers when drawn at {size}");
            }
        }
    }

    [Fact]
    public void The_calendar_icons_are_drawn()
    {
        var drawn = Drawn();
        foreach (var name in new[] { "check", "x", "chevron-left", "chevron-right" }) Assert.Contains(name, drawn);
    }

    // The redesign: every icon is black in both themes and sits on a light chip, so dark mode never draws black on black.
    [Fact]
    public void Icon_view_is_black_in_both_themes()
    {
        var source = File.ReadAllText(AppFile("Controls", "Icons.cs"));
        Assert.Contains("\"IconInk\"", source);
        Assert.Contains("\"IconChipDark\"", source);
        Assert.DoesNotContain("InkDark", source);
        Assert.DoesNotContain("OnInk", source);
    }

    [Fact]
    public void Bell_is_drawn_by_the_shared_icon_view()
    {
        var header = File.ReadAllText(AppFile("Controls", "HeaderBar.xaml"));
        Assert.Contains("Icon=\"bell\"", header);
        Assert.DoesNotContain("<Path", header);
        Assert.Contains("bell", Drawn());
    }

    [Fact]
    public void Selected_choice_buttons_are_lime_not_black()
    {
        var source = File.ReadAllText(AppFile("Controls", "Icons.cs"));
        Assert.Contains("\"LimeSmallButton\"", source);
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
