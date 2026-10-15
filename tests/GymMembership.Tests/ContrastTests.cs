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
        foreach (var (theme, ink, muted, inverse, inverseOn) in new[] { ("Light", "InkLight", "MutedLight", "White", "InkLight"), ("Dark", "InkDark", "MutedDark", "ChalkDark", "InkDark") })
        {
            foreach (var bg in new[] { "Chalk" + theme, "Surface" + theme }) { yield return new object[] { ink, bg }; yield return new object[] { muted, bg }; }
            yield return new object[] { inverse, inverseOn };   // button text, avatar letters
        }
        yield return new object[] { "White", "PlateRedLight" };       // unread badge, light
        yield return new object[] { "ChalkDark", "PlateRedDark" };    // unread badge, dark
    }

    public static IEnumerable<object[]> IndicatorPairs() =>
        from theme in new[] { "Light", "Dark" }
        from plate in new[] { "PlateBlue", "PlateYellow", "PlateGreen", "PlateRed" }
        from bg in new[] { "Chalk", "Surface" }
        select new object[] { plate + theme, bg + theme };

    [Theory, MemberData(nameof(TextPairs))]
    public void Text_pairs_reach_4_5_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 4.5, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");

    [Theory, MemberData(nameof(IndicatorPairs))]
    public void Status_colors_reach_3_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 3.0, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");
}
