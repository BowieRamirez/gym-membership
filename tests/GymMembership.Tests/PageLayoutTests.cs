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

    // a narrow Windows window gets one column instead of two squeezed ones (the column count is a maximum, not a promise)
    [Fact]
    public void Card_grid_drops_columns_that_would_be_too_narrow()
    {
        var controls = File.ReadAllText(AppFile("Controls", "Controls.cs"));
        var grid = controls[controls.IndexOf("public class CardGrid", StringComparison.Ordinal)..controls.IndexOf("public class CardTitle", StringComparison.Ordinal)];
        Assert.Contains("MinColumnWidth", grid);
        Assert.Contains("int Fit(double width)", grid);
        Assert.Equal(2, grid.Split("Fit(").Length - 2);   // used by both Measure and ArrangeChildren
    }

    // full-page layout: the side panel sits on the right only when there is room; narrower, it stacks above the main area (form first, as before)
    [Fact]
    public void Side_layout_stacks_side_first_when_narrow()
    {
        var controls = File.ReadAllText(AppFile("Controls", "Controls.cs"));
        var side = controls[controls.IndexOf("public class SideLayout", StringComparison.Ordinal)..controls.IndexOf("public class CardGrid", StringComparison.Ordinal)];
        Assert.Contains("WideFrom", side);
        Assert.Equal(2, side.Split("new[] { side, main }").Length - 1);   // Measure and ArrangeChildren both put the side first
        var styles = File.ReadAllText(AppFile("Resources", "Styles", "Styles.xaml"));
        foreach (var key in new[] { "x:Key=\"Kpi\"", "x:Key=\"TablePanel\"", "x:Key=\"TableRow\"", "x:Key=\"SidePanel\"", "x:Key=\"Toolbar\"" })
            Assert.Contains(key, styles);
    }

    // a style based on one defined further down the file fails at runtime and closes the app at start-up (it builds fine)
    [Fact]
    public void Styles_are_based_only_on_styles_defined_above_them()
    {
        var styles = File.ReadAllText(AppFile("Resources", "Styles", "Styles.xaml"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(styles, "BasedOn=\"\\{StaticResource (\\w+)\\}\""))
        {
            var defined = styles.IndexOf($"x:Key=\"{m.Groups[1].Value}\"", StringComparison.Ordinal);
            Assert.True(defined >= 0 && defined < m.Index, $"BasedOn {m.Groups[1].Value} is used before it is defined");
        }
    }

    // UI scripts find card headings by AutomationId; the id has to reach the text the automation tree exposes
    [Fact]
    public void Card_title_passes_its_automation_id_to_its_text()
    {
        var controls = File.ReadAllText(AppFile("Controls", "Controls.cs"));
        Assert.Contains("_label.AutomationId = AutomationId", controls);
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
