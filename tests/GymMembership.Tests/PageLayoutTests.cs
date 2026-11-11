using Xunit;

/// <summary>The redesign's page layout: wide canvas, plain card titles, panels with dividers, one highlight card whose words flip with it, pill lanes.</summary>
public class PageLayoutTests
{
    const string OnHero = "TextColor=\"{AppThemeBinding Light={StaticResource White}, Dark={StaticResource Black}}\"";

    static string AppFile(params string[] parts) => Path.Combine(new[] { TestPaths.AppFolder() }.Concat(parts).ToArray());

    [Fact]
    public void Member_sessions_hosts_calendar_and_it_has_no_separate_menu_item()
    {
        var xaml = File.ReadAllText(AppFile("Views", "SessionsPage.xaml"));
        var code = File.ReadAllText(AppFile("Views", "SessionsPage.xaml.cs"));
        var shell = File.ReadAllText(AppFile("MemberShell.xaml"));
        Assert.True(xaml.IndexOf("</c:SideLayout>", StringComparison.Ordinal) < xaml.IndexOf("x:Name=\"CalendarSection\"", StringComparison.Ordinal));
        Assert.Contains("x:Name=\"CalendarHost\"", xaml);
        Assert.Contains("Shell.Current is MemberShell", code);
        Assert.Contains("HostBelow(CalendarHost", code);
        Assert.Contains("RefreshOwnViewModel => false", code);
        Assert.DoesNotContain("Title=\"Calendar\"", shell);
        var calendar = File.ReadAllText(AppFile("Views", "MemberCalendarPage.xaml"));
        Assert.Contains("<VerticalStackLayout.Resources>", calendar);
        Assert.Contains("x:Key=\"TileWidth\"", calendar);
    }

    [Fact]
    public void Cashier_shift_and_sale_are_in_main_column_with_order_beside_them()
    {
        var doc = System.Xml.Linq.XDocument.Load(AppFile("Views", "CashierPage.xaml"));
        var side = doc.Descendants().Single(e => e.Name.LocalName == "SideLayout");
        var columns = side.Elements().ToArray();
        Assert.Equal(2, columns.Length);
        Assert.Equal("VerticalStackLayout", columns[0].Name.LocalName);
        Assert.Contains(columns[0].Elements(), e => (string?)e.Attribute("IsVisible") == "{Binding NeedsShift}");
        Assert.Contains(columns[0].Elements(), e => (string?)e.Attribute("IsVisible") == "{Binding HasShift}");
        Assert.Contains(columns[0].Elements(), e => (string?)e.Attribute("Text") == "What are they paying for?");
        Assert.Contains(columns[1].Descendants(), e => (string?)e.Attribute("Text") == "Order");
        Assert.Equal("False", (string?)side.Attribute("SideFirst"));
    }

    [Fact]
    public void Shared_pieces_exist()
    {
        var controls = File.ReadAllText(AppFile("Controls", "Controls.cs"));
        Assert.Contains("public class CardTitle", controls);
        var styles = File.ReadAllText(AppFile("Resources", "Styles", "Styles.xaml"));
        foreach (var key in new[] { "x:Key=\"Row\"", "x:Key=\"HeroPanel\"", "x:Key=\"HeroButton\"", "x:Key=\"HeroSmallButton\"", "x:Key=\"PageWidth\"", "x:Key=\"SectionTitle\"" })
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
        foreach (var key in new[] { "x:Key=\"TablePanel\"", "x:Key=\"TableRow\"", "x:Key=\"SidePanel\"" })
            Assert.Contains(key, styles);
    }

    // a table squeezed below the room its fixed columns need wraps text a letter per line: every table switches to its cards by width
    [Fact]
    public void Tables_switch_to_cards_by_their_own_width()
    {
        var controls = File.ReadAllText(AppFile("Controls", "Controls.cs"));
        Assert.Contains("public class TableBody", controls);
        foreach (var page in Directory.GetFiles(Path.GetDirectoryName(AppFile("Views", "SessionsPage.xaml"))!, "*.xaml"))
        {
            var xaml = File.ReadAllText(page);
            var heads = xaml.Split("StaticResource TableHead}").Length - 1;
            Assert.True(heads == xaml.Split("<c:TableBody ").Length - 1, $"{Path.GetFileName(page)}: every table head needs a TableBody");
        }
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

    // the highlight card is black in light and yellow in dark (Phase 9, D23): its words and buttons flip with it
    [Fact]
    public void Highlight_cards_hold_only_flipping_text_and_hero_buttons()
    {
        foreach (var file in Directory.GetFiles(AppFile("Views"), "*.xaml"))
        {
            var text = File.ReadAllText(file);
            foreach (var part in text.Split("Style=\"{StaticResource HeroPanel}\"").Skip(1))
            {
                var card = part[..part.IndexOf("</Border>", StringComparison.Ordinal)];
                foreach (var label in card.Split("<Label").Skip(1).Select(l => l[..l.IndexOf('>')]))
                    Assert.True(label.Contains(OnHero), $"{Path.GetFileName(file)}: highlight-card label without the flipping colour: {label}");
                foreach (var button in card.Split("<Button").Skip(1).Select(b => b[..b.IndexOf('>')]))
                    Assert.Contains("HeroSmallButton", button);
            }
        }
    }

    [Fact]
    public void Coach_profile_has_the_form_beside_check_in_and_one_picture_button()
    {
        var xaml = File.ReadAllText(AppFile("Views", "CoachProfilePage.xaml"));
        Assert.DoesNotContain("c:CardGrid", xaml);   // the form no longer sits alone in half a row
        Assert.Contains("<c:SideLayout x:Name=\"CoachColumn\"", xaml);   // form, with check-in beside it
        Assert.DoesNotContain("CoachPhoto", xaml);   // owner, 2026-11-11: one picture button, Account's "Change picture"
        // owner's sketch, 2026-11-11: Account first, then the coach profile under name and email, Theme and password in the right column
        var settings = File.ReadAllText(AppFile("Views", "SettingsPage.xaml"));
        Assert.True(settings.IndexOf("Text=\"Sign-in email\"", StringComparison.Ordinal) < settings.IndexOf("x:Name=\"CoachSlot\"", StringComparison.Ordinal));
        Assert.True(settings.IndexOf("x:Name=\"CoachSlot\"", StringComparison.Ordinal) < settings.IndexOf("Text=\"Theme\"", StringComparison.Ordinal));
        Assert.Contains("settings.CoachSlot.Content = CoachSection", File.ReadAllText(AppFile("Views", "CoachProfilePage.xaml.cs")));
    }
}
