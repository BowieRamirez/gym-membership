#if WINDOWS || ANDROID
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public sealed class CoachOverviewPage : BasePage
{
    public CoachOverviewPage(CoachOverviewViewModel vm)
    {
        Title = "Overview";
        BindingContext = vm;
#if ANDROID
        var body = new VerticalStackLayout { Padding = new Thickness(16, 20, 16, 24), Spacing = 16 };
#else
        var body = new VerticalStackLayout { Padding = new Thickness(24, 20, 24, 32), Spacing = 16 };
#endif
        body.Children.Add(Text("Coach overview", "Title", "CoachOverviewTitle"));
        body.Children.Add(Text("Your coaching day at a glance. A read-only summary of your sessions, trainees and activity.", "Muted"));
        var status = new VerticalStackLayout { Spacing = 8 };
        var updated = Text("", "Caption", "CoachOverviewUpdated");
        updated.SetBinding(Label.TextProperty, nameof(vm.UpdatedText));
        status.Children.Add(updated);
        var refresh = new Button { Text = "Refresh overview", AutomationId = "CoachOverviewRefresh", HorizontalOptions = LayoutOptions.Start, Command = vm.RefreshCommand };
        status.Children.Add(refresh);
        var busy = new ActivityIndicator();
        busy.SetBinding(ActivityIndicator.IsRunningProperty, nameof(vm.IsBusy));
        busy.SetBinding(IsVisibleProperty, nameof(vm.IsBusy));
        status.Children.Add(busy);
        body.Children.Add(status);
        body.Children.Add(Panel(vm.Summaries[0]));

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        foreach (var summary in vm.Summaries.Skip(1)) grid.Children.Add(Panel(summary));
        var columns = 0;
        grid.SizeChanged += (_, _) =>
        {
            var next = grid.Width >= 1000 ? 3 : grid.Width >= 650 ? 2 : 1;
            if (next == columns) return;
            columns = next;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < (grid.Children.Count + columns - 1) / columns; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < grid.Children.Count; i++)
            {
                grid.SetColumn(grid.Children[i], i % columns);
                grid.SetRow(grid.Children[i], i / columns);
            }
        };
        body.Children.Add(grid);
        body.Children.Add(Text("Dates and times follow this device, as in your attendance calendar. Counts reflect loaded records and may be limited by the server. Refresh or reopen this page for updates.", "Caption"));
        Content = new ScrollView { Content = body, MaximumWidthRequest = 1240, HorizontalOptions = LayoutOptions.Fill };
    }

    static Border Panel(CoachOverviewSummary summary)
    {
        var content = new VerticalStackLayout { Spacing = 10 };
        content.Children.Add(Text(summary.Title, "Heading", "CoachOverviewHeading-" + summary.Key));
        var value = Text("", "Title", "CoachOverviewValue-" + summary.Key);
        value.SetBinding(Label.TextProperty, new Binding(nameof(summary.Value), source: summary));
        content.Children.Add(value);
        var detail = Text("", "Caption", "CoachOverviewDetail-" + summary.Key);
        detail.SetBinding(Label.TextProperty, new Binding(nameof(summary.Detail), source: summary));
        content.Children.Add(detail);
        return new Border { Style = Resource<Style>("Panel"), Content = content };
    }

    static T Resource<T>(string key) => (T)Application.Current!.Resources[key];
    static Label Text(string text, string style, string? id = null) => new() { Text = text, Style = Resource<Style>(style), AutomationId = id };
}
#endif
