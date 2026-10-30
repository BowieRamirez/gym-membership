#if WINDOWS
using GymMembership.Core.ViewModels;
using GymMembership.Core.Services;

namespace GymMembership.App.Views;

public sealed class AdminOverviewPage : BasePage
{
    /// <param name="superAdmin">False for a sub-admin (D15): only the cards for pages they have.</param>
    public AdminOverviewPage(AdminOverviewViewModel vm, bool superAdmin = true)
    {
        vm.SuperAdmin = superAdmin;
        Title = "Overview";
        BindingContext = vm;
        var body = new VerticalStackLayout { Padding = new Thickness(24, 20, 24, 32), Spacing = 16 };
        body.Children.Add(Text("Admin overview", "Title", "AdminOverviewTitle"));
        body.Children.Add(Text("Your administration pages at a glance. Choose a page to review or make changes.", "Muted"));

        var status = new VerticalStackLayout { Spacing = 8 };
        var updated = Text("", "Caption", "AdminOverviewUpdated");
        updated.SetBinding(Label.TextProperty, nameof(vm.UpdatedText));
        status.Children.Add(updated);
        var refresh = new Button { Text = "Refresh overview", AutomationId = "AdminOverviewRefresh", HorizontalOptions = LayoutOptions.Start };
        refresh.Command = vm.RefreshCommand;
        status.Children.Add(refresh);
        var busy = new ActivityIndicator();
        busy.SetBinding(ActivityIndicator.IsRunningProperty, nameof(vm.IsBusy));
        busy.SetBinding(IsVisibleProperty, nameof(vm.IsBusy));
        status.Children.Add(busy);
        body.Children.Add(status);

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        foreach (var card in vm.Shown)
        {
            var content = new VerticalStackLayout { Spacing = 10 };
            // the card header names the page with the same icon the side menu uses
            content.Children.Add(new Controls.CardTitle { Icon = Controls.IconRail.PageIcons.GetValueOrDefault(card.Title, "layout-dashboard"), Text = card.Title, AutomationId = "AdminOverviewHeading-" + card.Key });
            var value = Text("", "Title", "AdminOverviewValue-" + card.Key);
            value.SetBinding(Label.TextProperty, new Binding(nameof(card.Value), source: card));
            content.Children.Add(value);
            var detail = Text("", "Caption");
            detail.SetBinding(Label.TextProperty, new Binding(nameof(card.Detail), source: card));
            content.Children.Add(detail);
            var open = new Button { Text = "Open " + card.Title, AutomationId = "AdminOverviewOpen-" + card.Key, HorizontalOptions = LayoutOptions.Fill };
            open.Clicked += (_, _) => (Shell.Current as StaffShell)?.ShowAdminPage(card.Title);
            var actions = new VerticalStackLayout { Spacing = 10 };
            var retry = new Button { Text = "Retry summaries", Command = vm.RefreshCommand };
            retry.SetBinding(IsVisibleProperty, new Binding(nameof(card.IsUnavailable), source: card));
            actions.Children.Add(retry);
            actions.Children.Add(open);
            var cardLayout = new Grid
            {
                RowSpacing = 10,
                RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) }
            };
            cardLayout.Children.Add(content);
            cardLayout.Children.Add(actions);
            cardLayout.SetRow(actions, 1);
            grid.Children.Add(new Border { Style = Resource<Style>("Panel"), Content = cardLayout });
        }
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
        body.Children.Add(Text("List-based counts reflect the records loaded by their admin pages; they are not guaranteed database-wide totals.", "Caption"));
        // D12: who came in on a chosen day (member QR check-ins and name-search check-ins alike)
        body.Children.Add(Text("Check-ins", "SectionTitle", "AdminOverviewCheckIns"));
        var day = new DatePicker { MaximumDate = DateTime.Today.AddDays(1), HorizontalOptions = LayoutOptions.Start, AutomationId = "AdminCheckInDay" };
        day.SetBinding(DatePicker.DateProperty, nameof(vm.CheckInDay));
        body.Children.Add(new Border { Style = Resource<Style>("Field"), Content = day, HorizontalOptions = LayoutOptions.Start });
        var count = Text("", "Heading", "AdminCheckInCount");
        count.SetBinding(Label.TextProperty, nameof(vm.CheckInsText));
        body.Children.Add(count);
        var list = new VerticalStackLayout { Spacing = 4 };
        BindableLayout.SetItemsSource(list, vm.CheckIns);
        BindableLayout.SetItemTemplate(list, new DataTemplate(() =>
        {
            var row = new Label { Style = Resource<Style>("Muted") };
            row.SetBinding(Label.TextProperty, new MultiBinding { StringFormat = "{0}  {1}", Bindings = { new Binding(nameof(CheckInRow.TimeText)), new Binding(nameof(CheckInRow.Name)) } });
            return row;
        }));
        body.Children.Add(list);
        body.Children.Add(Text("Recent audit activity", "SectionTitle"));
        var activity = Text("", "Muted", "AdminOverviewActivity");
        activity.SetBinding(Label.TextProperty, nameof(vm.RecentActivity));
        body.Children.Add(activity);
        Content = new ScrollView { Content = body, MaximumWidthRequest = 1240, HorizontalOptions = LayoutOptions.Fill };
    }

    static T Resource<T>(string key) => (T)Application.Current!.Resources[key];
    static Label Text(string text, string style, string? id = null) => new() { Text = text, Style = Resource<Style>(style), AutomationId = id };
}
#endif
