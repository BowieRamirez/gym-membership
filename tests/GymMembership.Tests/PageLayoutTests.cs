using Xunit;

/// <summary>The redesign's page layout: wide canvas, card titles with icon chips, white rows, a lime card with black text, pill lanes.</summary>
public class PageLayoutTests
{
    static string AppFile(params string[] parts) => Path.Combine(new[] { TestPaths.AppFolder() }.Concat(parts).ToArray());

    [Fact]
    public void Shared_pieces_exist()
    {
        var controls = File.ReadAllText(AppFile("Controls", "Controls.cs"));
        Assert.Contains("public class CardTitle", controls);
        var styles = File.ReadAllText(AppFile("Resources", "Styles", "Styles.xaml"));
        foreach (var key in new[] { "x:Key=\"Row\"", "x:Key=\"HeroPanel\"", "x:Key=\"InkButton\"", "x:Key=\"InkSmallButton\"", "x:Key=\"PageWidth\"", "x:Key=\"SectionTitle\"" })
            Assert.Contains(key, styles);
    }

    [Fact]
    public void Lane_is_drawn_as_pills()
    {
        var controls = File.ReadAllText(AppFile("Controls", "Controls.cs"));
        var lane = controls[controls.IndexOf("sealed class LaneDrawable", StringComparison.Ordinal)..];
        Assert.Contains("FillRoundedRectangle", lane);
    }

    [Fact]
    public void No_page_keeps_the_old_narrow_column()
    {
        foreach (var file in Directory.GetFiles(AppFile("Views"), "*.xaml"))
            Assert.DoesNotContain("MaximumWidthRequest=\"920\"", File.ReadAllText(file));
    }

    // a lime card keeps its words black in dark mode too
    [Fact]
    public void Lime_cards_hold_only_black_text_and_ink_buttons()
    {
        foreach (var file in Directory.GetFiles(AppFile("Views"), "*.xaml"))
        {
            var text = File.ReadAllText(file);
            foreach (var part in text.Split("Style=\"{StaticResource HeroPanel}\"").Skip(1))
            {
                var card = part[..part.IndexOf("</Border>", StringComparison.Ordinal)];
                foreach (var label in card.Split("<Label").Skip(1).Select(l => l[..l.IndexOf('>')]))
                    Assert.True(label.Contains("TextColor=\"{StaticResource Black}\""), $"{Path.GetFileName(file)}: lime-card label without black text: {label}");
                foreach (var button in card.Split("<Button").Skip(1).Select(b => b[..b.IndexOf('>')]))
                    Assert.Contains("Ink", button);
            }
        }
    }
}
