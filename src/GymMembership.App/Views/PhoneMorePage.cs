using GymMembership.App.Controls;

namespace GymMembership.App.Views;

/// <summary>The phone's More tab: the pages that don't fit the tab bar, as cards like the rest of the app
/// (an icon chip, the title, a chevron). Colours and the card come from the shared palette and Panel style.</summary>
public sealed class PhoneMorePage : ContentPage
{
    static readonly Dictionary<string, string> Icons = new()
    {
        ["Amenities"] = "waves", ["Book an amenity"] = "calendar-plus", ["Coaches"] = "users", ["Time requests"] = "calendar-clock",
        ["My shifts"] = "calendar-days", ["My coach profile"] = "user-round",
    };

    public PhoneMorePage((string Title, string Route)[] destinations, Func<string, Task> open)
    {
        Title = "More";
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);
        var res = Application.Current!.Resources;
        this.SetAppThemeColor(BackgroundColorProperty, (Color)res["ChalkLight"], (Color)res["ChalkDark"]);
        var list = new VerticalStackLayout { Padding = new Thickness(16, 12, 16, 24), Spacing = 10 };
        foreach (var (title, route) in destinations)
        {
            var row = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 12 };
            row.Add(new IconView { Icon = Icons.GetValueOrDefault(title, "chevron-right"), Size = 18, ChipSize = 36, VerticalOptions = LayoutOptions.Center });
            var label = new Label { Text = title, FontFamily = "BarlowMedium", FontSize = 17, VerticalOptions = LayoutOptions.Center };
            label.SetAppThemeColor(Label.TextColorProperty, (Color)res["InkLight"], (Color)res["InkDark"]);
            row.Add(label, 1);
            var chevron = new Label { Text = "›", FontSize = 24, VerticalOptions = LayoutOptions.Center };
            chevron.SetAppThemeColor(Label.TextColorProperty, (Color)res["MutedLight"], (Color)res["MutedDark"]);
            row.Add(chevron, 2);
            // the card is drawn by the Panel style; a bare button on top gives touch, focus and the screen-reader name
            var button = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0 };
            SemanticProperties.SetDescription(button, title);
            button.Clicked += async (_, _) => await open(route);
            var card = new Grid { MinimumHeightRequest = 56, Children = { new Border { Style = (Style)res["Panel"], Content = row }, button } };
            list.Add(card);
        }
        Content = new ScrollView { Content = list };
    }
}
