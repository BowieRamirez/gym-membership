using System.Xml.Linq;
using Xunit;

/// <summary>The color tokens must stay readable: WCAG AA is 4.5:1 for text and 3:1 for indicators such as status dots.</summary>
public class ContrastTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2009/xaml";
    static readonly Lazy<Dictionary<string, string>> Tokens = new(() =>
        XDocument.Load(Path.Combine(TestPaths.AppFolder(), "Resources", "Styles", "Colors.xaml")).Descendants()
            .Where(e => e.Name.LocalName == "Color" && e.Attribute(X + "Key") is not null)
            .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => e.Value.Trim()));

    static double Luminance(string hex)
    {
        double Channel(int i) { var v = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    static double Ratio(string a, string b)
    {
        var (la, lb) = (Luminance(Tokens.Value[a]), Luminance(Tokens.Value[b]));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static IEnumerable<object[]> TextPairs()
    {
        foreach (var (theme, ink, muted, inverse, inverseOn) in new[] { ("Light", "InkLight", "MutedLight", "White", "InkLight"), ("Dark", "InkDark", "MutedDark", "Black", "Lime") })
        {
            foreach (var bg in new[] { "Chalk" + theme, "Surface" + theme, "Card" + theme }) { yield return new object[] { ink, bg }; yield return new object[] { muted, bg }; }
            yield return new object[] { inverse, inverseOn };   // button text: white on black (light), black on lime (dark)
        }
        yield return new object[] { "White", "InkLight" };            // avatar letters, light
        yield return new object[] { "ChalkDark", "InkDark" };         // avatar letters, dark
        yield return new object[] { "Black", "Lime" };                // lime highlight, lime buttons
        yield return new object[] { "IconInk", "IconChipLight" };     // icons sit on a light chip in both themes
        yield return new object[] { "IconInk", "IconChipDark" };
        yield return new object[] { "White", "PlateRedLight" };       // unread badge, light
        yield return new object[] { "ChalkDark", "PlateRedDark" };    // unread badge, dark
    }

    public static IEnumerable<object[]> IndicatorPairs() =>
        from theme in new[] { "Light", "Dark" }
        from plate in new[] { "PlateBlue", "PlateYellow", "PlateGreen", "PlateRed" }
        from bg in new[] { "Chalk", "Surface", "Card" }
        select new object[] { plate + theme, bg + theme };

    [Theory, MemberData(nameof(TextPairs))]
    public void Text_pairs_reach_4_5_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 4.5, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");

    // The redesign: pages carry no colours from the old palette, selected choices are lime, and dashed lines only show progress.
    [Fact]
    public void No_page_uses_the_old_palette()
    {
        var app = TestPaths.AppFolder();
        var files = new[] { "Views", "Controls" }.SelectMany(d => Directory.GetFiles(Path.Combine(app, d)))
            .Concat(Directory.GetFiles(app, "*Shell*")).Where(f => f.EndsWith(".xaml") || f.EndsWith(".cs"));
        var old = new[] { "#1F2226", "#24282C", "#EDEFF1", "#ECEEF0", "#5C646C", "#9AA3AB", "#CDD3D9", "#3A4047", "#1A1D20" };
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.All(old, hex => Assert.DoesNotContain(hex, text, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Value=\"{StaticResource SmallButton}\"", text);   // a selected toggle is lime, not black
        }
        foreach (var page in new[] { "Views/SettingsPage.xaml", "MemberShell.xaml", "CoachShell.xaml", "StaffShell.xaml" })
            Assert.DoesNotContain("<c:LaneView", File.ReadAllText(Path.Combine(app, page)));
    }

    [Theory, MemberData(nameof(IndicatorPairs))]
    public void Status_colors_reach_3_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 3.0, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");
}
