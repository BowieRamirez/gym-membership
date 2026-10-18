namespace GymMembership.App.Views;

public sealed class PhoneMorePage : ContentPage
{
    public PhoneMorePage((string Title, string Route)[] destinations, Func<string, Task> open)
    {
        Title = "More";
        SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);
        this.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#EDEFF1"), Color.FromArgb("#1A1D20"));
        var list = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        var heading = new Label { Text = "More", FontFamily = "BarlowSemiBold", FontSize = 28 };
        heading.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1F2226"), Color.FromArgb("#ECEEF0"));
        list.Add(heading);
        foreach (var (title, route) in destinations)
        {
            var button = new Button
            {
                Text = title + "  ›", FontFamily = "BarlowMedium", FontSize = 17,
                MinimumHeightRequest = 56, CornerRadius = 16, Padding = new Thickness(20, 14),
                HorizontalOptions = LayoutOptions.Fill
            };
            button.SetAppThemeColor(Button.BackgroundColorProperty, Colors.White, Color.FromArgb("#24282C"));
            button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#1F2226"), Color.FromArgb("#ECEEF0"));
            SemanticProperties.SetDescription(button, title);
            button.Clicked += async (_, _) => await open(route);
            list.Add(button);
        }
        Content = new ScrollView { Content = list };
    }
}
