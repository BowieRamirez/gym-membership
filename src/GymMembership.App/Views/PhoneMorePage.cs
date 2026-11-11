using GymMembership.App.Controls;

namespace GymMembership.App.Views;

/// <summary>The phone's More tab: the pages that don't fit the tab bar, as rows of one plain panel with dividing lines
/// (Phase 9 page standard: a list is a panel, not a stack of cards). Each row: the page's icon, its title, a chevron.</summary>
public sealed class PhoneMorePage : ContentPage
{
    static readonly Dictionary<string, string> Icons = new()
    {
        ["Amenities"] = "waves", ["Book an amenity"] = "calendar-plus", ["Coaches"] = "users", ["Time requests"] = "calendar-clock",
        ["Profile & settings"] = "user-round", ["Renew or change"] = "package",
    };

    public PhoneMorePage((string Title, string Route)[] destinations, Func<string, Task> open)
    {
        Title = "More";
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);
        var res = Application.Current!.Resources;
        this.SetAppThemeColor(BackgroundColorProperty, (Color)res["PageLight"], (Color)res["PageDark"]);
        var rows = new VerticalStackLayout();
        foreach (var (title, route) in destinations)
        {
            if (rows.Count > 0) rows.Add(new BoxView { Style = (Style)res["Hairline"] });
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12, Padding = new Thickness(14, 10) };
            row.Add(new IconView { Icon = Icons.GetValueOrDefault(title, "chevron-right"), Size = 18, ChipSize = 36, VerticalOptions = LayoutOptions.Center });
            var label = new Label { Text = title, FontFamily = "BarlowMedium", FontSize = 17, VerticalOptions = LayoutOptions.Center };
            label.SetAppThemeColor(Label.TextColorProperty, (Color)res["InkLight"], (Color)res["InkDark"]);
            row.Add(label, 1);
            row.Add(new IconView { Icon = "chevron-right", Ghost = true, Size = 16, VerticalOptions = LayoutOptions.Center }, 2);
            // a bare button over the row gives touch, focus and the screen-reader name
            var button = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0 };
            SemanticProperties.SetDescription(button, title);
            button.Clicked += async (_, _) => await open(route);
            rows.Add(new Grid { MinimumHeightRequest = 56, Children = { row, button } });
        }
        var panel = new Border { Style = (Style)res["Panel"], Padding = 0, VerticalOptions = LayoutOptions.Start, Content = rows };
        Content = new ScrollView { Content = new ContentView { Padding = new Thickness(16, 12, 16, 24), Content = panel } };
    }
}
