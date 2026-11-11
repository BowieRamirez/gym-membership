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
        foreach (var (theme, ink, muted, inverse, inverseOn) in new[] { ("Light", "InkLight", "MutedLight", "White", "InkLight"), ("Dark", "InkDark", "MutedDark", "Black", "Signal") })
        {
            foreach (var bg in new[] { "Page" + theme, "Surface" + theme, "Card" + theme }) { yield return new object[] { ink, bg }; yield return new object[] { muted, bg }; }
            yield return new object[] { inverse, inverseOn };   // main button and highlight card: white on black (light), black on yellow (dark)
        }
        yield return new object[] { "White", "InkLight" };            // avatar letters, light
        yield return new object[] { "PageDark", "InkDark" };          // avatar letters, dark
        yield return new object[] { "Black", "Signal" };              // anything yellow: side menu (light), selected choices, highlight (dark)
        yield return new object[] { "InkLight", "RailLight" };        // side-menu icons on yellow (Phase 9, D23)
        yield return new object[] { "InkDark", "RailDark" };
        yield return new object[] { "IconInk", "IconChipLight" };     // icons: black on a light chip in light, light on a dark chip in dark
        yield return new object[] { "InkDark", "IconChipDark" };
        yield return new object[] { "White", "BadLight" };            // unread badge, light
        yield return new object[] { "PageDark", "BadDark" };          // unread badge, dark
        yield return new object[] { "MutedDark", "InkLight" };        // sign-in brand panel tagline, light (AuthLayout)
        yield return new object[] { "PendingLight", "CardLight" };    // pending and cooldown words, light (the old yellow failed on white)
        yield return new object[] { "MutedLight", "TakenLight" };     // "Booked" in a taken slot, light (Book an amenity)
        yield return new object[] { "MutedDark", "LineDark" };        // "Booked" in a taken slot, dark
    }

    public static IEnumerable<object[]> IndicatorPairs() =>
        from theme in new[] { "Light", "Dark" }
        from status in new[] { "Scheduled", "Pending", "Good", "Bad", "Edge" }   // Edge: a field's border, which must show as a control
        from bg in new[] { "Page", "Surface", "Card" }
        select new object[] { status + theme, bg + theme };

    // D23: the colours are the approved yellow-and-black values, so a stray edit can't drift them
    [Theory]
    [InlineData("Signal", "#FFEC00")]
    [InlineData("InkLight", "#111111")] [InlineData("PageLight", "#EEEEEE")] [InlineData("CardLight", "#FFFFFF")] [InlineData("RailLight", "#FFEC00")]
    [InlineData("InkDark", "#F2F2F2")] [InlineData("PageDark", "#0E0E0E")] [InlineData("CardDark", "#1A1A1A")] [InlineData("RailDark", "#161616")]
    public void The_palette_is_the_approved_yellow_and_black(string key, string hex) => Assert.Equal(hex, Tokens.Value[key], ignoreCase: true);

    [Theory, MemberData(nameof(TextPairs))]
    public void Text_pairs_reach_4_5_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 4.5, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");

    // The redesign: pages carry no colours from the old palette, selected choices are lime, and dashed lines only show progress.
    [Fact]
    public void No_page_uses_the_old_palette()
    {
        var app = TestPaths.AppFolder();
        var files = new[] { "Views", "Controls" }.SelectMany(d => Directory.GetFiles(Path.Combine(app, d)))
            .Concat(Directory.GetFiles(app, "*Shell*")).Where(f => f.EndsWith(".xaml") || f.EndsWith(".cs"))
            // Android's own resources too: a lime colorAccent survived there until 9.6 and tinted the switch
            .Concat(Directory.GetFiles(Path.Combine(app, "Platforms", "Android", "Resources"), "*.xml", SearchOption.AllDirectories));
        var old = new[] { "#1F2226", "#24282C", "#EDEFF1", "#ECEEF0", "#5C646C", "#9AA3AB", "#CDD3D9", "#3A4047", "#1A1D20",
            "#E4F58A", "#2C302B", "#EEF1EC", "#5A6159", "#A3AAA1", "#ECEFE8", "#212521", "#161816", "#D4DAD0", "#3A3F39" };   // + the sage and lime of 2026-10-30
        foreach (var gone in new[] { "Lime", "Chalk", "Plate" }) Assert.DoesNotContain($"x:Key=\"{gone}", File.ReadAllText(Path.Combine(app, "Resources", "Styles", "Colors.xaml")));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.All(old, hex => Assert.DoesNotContain(hex, text, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("Value=\"{StaticResource SmallButton}\"", text);   // a selected toggle is yellow, not black
            foreach (var gone in new[] { "StaticResource Lime", "\"Lime\"", "Chalk", "Plate" }) Assert.DoesNotContain(gone, text);
        }
        foreach (var page in new[] { "Views/SettingsPage.xaml", "MemberShell.xaml", "CoachShell.xaml", "StaffShell.xaml" })
            Assert.DoesNotContain("<c:LaneView", File.ReadAllText(Path.Combine(app, page)));
    }

    [Theory, MemberData(nameof(IndicatorPairs))]
    public void Status_colors_reach_3_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 3.0, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");
}
