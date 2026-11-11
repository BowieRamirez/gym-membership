#if WINDOWS
using GymMembership.App.Controls;
using GymMembership.Core.ViewModels;
using GymMembership.Core.Services;

namespace GymMembership.App.Views;

/// <summary>
/// Approved mockup: docs/mockups/admin-overview.html (Phase 7, D6). An attention bar only when refunds wait, four numbers that each open
/// their page, the day's check-ins, and the other pages as a short list. The side menu still reaches every page.
/// </summary>
public sealed class AdminOverviewPage : BasePage
{
    /// <param name="superAdmin">False for an Admin (sub-admin code): no refund bar, page list or activity, since those pages are System admin only.</param>
    public AdminOverviewPage(AdminOverviewViewModel vm, bool superAdmin = true)
    {
        vm.SuperAdmin = superAdmin;
        Title = "Overview";
        BindingContext = vm;
        var body = new VerticalStackLayout { Padding = new Thickness(16, 12, 16, 24), Spacing = 16 };

        // when it was checked, and refresh; the window header already says "Overview", as on the other admin pages
        var head = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) } };
        var updated = Text("", "Muted", "AdminOverviewUpdated");
        updated.VerticalOptions = LayoutOptions.Center;
        updated.SetBinding(Label.TextProperty, nameof(vm.UpdatedText));
        head.Add(updated);
        var busy = new ActivityIndicator { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center };
        busy.SetBinding(ActivityIndicator.IsRunningProperty, nameof(vm.IsBusy));
        busy.SetBinding(IsVisibleProperty, nameof(vm.IsBusy));
        head.Add(busy, 1);
        head.Add(Tap(new IconView { Icon = "refresh-cw", Size = 16, ChipSize = 36 }, "AdminOverviewRefresh", "Refresh overview", vm.RefreshCommand), 2);
        body.Children.Add(head);

        if (superAdmin)
        {
            var attention = new Grid { ColumnSpacing = 14, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
            var say = new Label { Style = Resource<Style>("Heading"), VerticalOptions = LayoutOptions.Center, AutomationId = "AdminOverviewAttention" };
            say.SetAppThemeColor(Label.TextColorProperty, Theme("White"), Theme("Black"));   // the bar is black (light) or yellow (dark)
            say.SetBinding(Label.TextProperty, nameof(vm.AttentionText));
            attention.Add(say);
            var review = new Button { Text = "Review refunds", Style = Resource<Style>("HeroSmallButton"), AutomationId = "AdminOverviewReview", VerticalOptions = LayoutOptions.Center };
            review.Clicked += (_, _) => Open("Refunds & voids");
            attention.Add(review, 1);
            var bar = new Border { Style = Resource<Style>("HeroPanel"), Padding = new Thickness(18, 10, 10, 10), Content = attention };
            bar.SetBinding(IsVisibleProperty, nameof(vm.HasAttention));
            body.Children.Add(bar);
        }

        // the number strip: the grid's line-coloured background shows through the 1 px gaps as dividers
        var strip = new Grid { ColumnSpacing = 1, RowSpacing = 1 };
        strip.SetAppThemeColor(BackgroundColorProperty, Theme("LineLight"), Theme("LineDark"));
        foreach (var card in vm.Figures) strip.Children.Add(Figure(card, vm));
        var columns = 0;
        strip.SizeChanged += (_, _) =>
        {
            var next = strip.Width >= 760 ? 4 : 2;
            if (next == columns || strip.Width <= 0) return;
            columns = next;
            strip.ColumnDefinitions.Clear();
            strip.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) strip.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < 4 / columns; i++) strip.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < strip.Children.Count; i++) { strip.SetColumn(strip.Children[i], i % columns); strip.SetRow(strip.Children[i], i / columns); }
        };
        body.Children.Add(new Border { Style = Resource<Style>("TablePanel"), Content = strip });

        var checkIns = CheckIns(vm);
        if (!superAdmin) body.Children.Add(checkIns);
        else
        {
            var side = new VerticalStackLayout { Spacing = 16 };
            side.Children.Add(Links(vm));
            side.Children.Add(Activity(vm));
            var lower = new Grid { ColumnSpacing = 22, RowSpacing = 16 };
            lower.Add(checkIns);
            lower.Add(side);
            var wide = (bool?)null;
            lower.SizeChanged += (_, _) =>
            {
                var now = lower.Width >= 1000;
                if (now == wide || lower.Width <= 0) return;
                wide = now;
                lower.ColumnDefinitions.Clear();
                lower.RowDefinitions.Clear();
                lower.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                if (now) lower.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                else { lower.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); lower.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }
                lower.SetColumn(side, now ? 1 : 0);
                lower.SetRow(side, now ? 0 : 1);
            };
            body.Children.Add(lower);
        }
        Content = new ScrollView { Content = body, MaximumWidthRequest = 2400, HorizontalOptions = LayoutOptions.Fill };
    }

    static View Figure(AdminOverviewCard card, AdminOverviewViewModel vm)
    {
        var stack = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(18, 14, 18, 16), InputTransparent = true };
        var label = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        label.Add(Text(card.Label!, "Muted"));
        label.Add(new IconView { Icon = "chevron-right", Ghost = true, Size = 14 }, 1);
        stack.Children.Add(label);
        var value = Text("", "KpiValue", "AdminOverviewValue-" + card.Key);
        value.Margin = new Thickness(0, 6, 0, 0);
        value.SetBinding(Label.TextProperty, new Binding(nameof(card.Value), source: card));
        stack.Children.Add(value);
        var detail = Text("", "Muted");
        detail.SetBinding(Label.TextProperty, new Binding(nameof(card.Detail), source: card));
        stack.Children.Add(detail);
        var room = new BoxView { HeightRequest = 40, Color = Colors.Transparent };   // keeps Retry off the text
        room.SetBinding(IsVisibleProperty, new Binding(nameof(card.IsUnavailable), source: card));
        stack.Children.Add(room);

        var cell = new Grid();
        cell.SetAppThemeColor(BackgroundColorProperty, Theme("CardLight"), Theme("CardDark"));
        cell.Add(stack);
        cell.Add(Overlay(card));
        var retry = new Button { Text = "Retry", Style = Resource<Style>("SmallOutlineButton"), Command = vm.RefreshCommand, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.End, Margin = new Thickness(18, 0, 0, 14) };
        retry.SetBinding(IsVisibleProperty, new Binding(nameof(card.IsUnavailable), source: card));
        cell.Add(retry);
        return cell;
    }

    static View Links(AdminOverviewViewModel vm)
    {
        var list = new VerticalStackLayout();
        foreach (var card in vm.Links)
        {
            if (list.Children.Count > 0) list.Children.Add(new BoxView { Style = Resource<Style>("Hairline") });
            var row = new Grid { Style = Resource<Style>("TableRow"), Padding = new Thickness(14, 10), InputTransparent = true, ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) } };
            row.Add(new IconView { Icon = IconRail.PageIcons.GetValueOrDefault(card.Title, "layout-dashboard"), Size = 16, ChipSize = 32, VerticalOptions = LayoutOptions.Center });
            var words = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center };
            words.Children.Add(Text(card.Title, "Heading"));
            var value = Text("", "Muted", "AdminOverviewValue-" + card.Key);
            value.SetBinding(Label.TextProperty, new Binding(nameof(card.Value), source: card));
            words.Children.Add(value);
            var why = Text("", "Caption");
            why.SetBinding(Label.TextProperty, new Binding(nameof(card.Detail), source: card));
            why.SetBinding(IsVisibleProperty, new Binding(nameof(card.IsUnavailable), source: card));
            words.Children.Add(why);
            row.Add(words, 1);
            row.Add(new IconView { Icon = "chevron-right", Ghost = true, Size = 14, VerticalOptions = LayoutOptions.Center }, 2);
            list.Children.Add(new Grid { Children = { row, Overlay(card) } });
        }
        return Section("Other pages", null, new Border { Style = Resource<Style>("TablePanel"), Content = list });
    }

    static View Activity(AdminOverviewViewModel vm)
    {
        var rows = new VerticalStackLayout();
        BindableLayout.SetItemsSource(rows, vm.Activity);
        BindableLayout.SetItemTemplate(rows, new DataTemplate(() =>
        {
            var row = new Grid { Style = Resource<Style>("TableRow"), ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
            var what = new Label { Style = Resource<Style>("BaseLabel"), VerticalOptions = LayoutOptions.Center, FormattedText = new FormattedString() };
            var action = new Span { FontFamily = "BarlowMedium" };
            action.SetBinding(Span.TextProperty, nameof(ActivityRow.Action));
            var actor = new Span();
            actor.SetAppThemeColor(Span.TextColorProperty, Theme("MutedLight"), Theme("MutedDark"));
            actor.SetBinding(Span.TextProperty, new Binding(nameof(ActivityRow.Actor), stringFormat: "  by {0}"));
            what.FormattedText.Spans.Add(action);
            what.FormattedText.Spans.Add(actor);
            row.Add(what);
            var when = Text("", "Muted");
            when.VerticalOptions = LayoutOptions.Center;
            when.SetBinding(Label.TextProperty, nameof(ActivityRow.WhenText));
            row.Add(when, 1);
            return new VerticalStackLayout { Children = { new BoxView { Style = Resource<Style>("Hairline") }, row } };
        }));
        var message = Text("", "Muted", "AdminOverviewActivity");
        message.Margin = new Thickness(14, 12);
        message.SetBinding(Label.TextProperty, nameof(vm.RecentActivity));
        message.SetBinding(IsVisibleProperty, nameof(vm.HasActivityMessage));
        // the first row's hairline would sit on the panel's top edge
        rows.ChildAdded += (_, _) => { if (rows.Children.FirstOrDefault() is VerticalStackLayout first) ((View)first.Children[0]).IsVisible = false; };
        return Section("Recent activity", null, new Border { Style = Resource<Style>("TablePanel"), Content = new VerticalStackLayout { Children = { message, rows } } });
    }

    static View CheckIns(AdminOverviewViewModel vm)
    {
        // D12: who came in on a chosen day, and how (QR, by name, or sent later from an offline desk).
        // F8: who recorded each and why a PIN was skipped; "By name only" is the Manual check-ins list (mockup docs/mockups/desk-pin.html)
        var bar = new Grid { Padding = new Thickness(14, 10), ColumnSpacing = 14, ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) } };
        var day = new DatePicker { MaximumDate = DateTime.Today.AddDays(1), AutomationId = "AdminCheckInDay" };
        day.SetBinding(DatePicker.DateProperty, nameof(vm.CheckInDay));
        bar.Add(new Border { Style = Resource<Style>("Field"), Content = day });
        var byName = new Switch { AutomationId = "AdminCheckInsByName", VerticalOptions = LayoutOptions.Center };
        SemanticProperties.SetDescription(byName, "Show only check-ins made by name");
        byName.SetBinding(Switch.IsToggledProperty, nameof(vm.ByNameOnly));
        bar.Add(new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Center, Children = { byName, Text("By name only", "BaseLabel") } }, 1);
        var count = Text("", "Heading", "AdminCheckInCount");
        count.HorizontalOptions = LayoutOptions.End;
        count.VerticalOptions = LayoutOptions.Center;
        count.SetBinding(Label.TextProperty, nameof(vm.CheckInsText));
        bar.Add(count, 2);
        var list = new VerticalStackLayout();
        BindableLayout.SetItemsSource(list, vm.CheckIns);
        BindableLayout.SetItemTemplate(list, new DataTemplate(() =>
        {
            var row = new Grid { Style = Resource<Style>("TableRow"), ColumnDefinitions = { new(52), new(GridLength.Star), new(GridLength.Auto) } };
            row.Add(Bound("Muted", nameof(CheckInRow.TimeText)));
            var by = Bound("Caption", nameof(CheckInRow.ByText));
            by.SetBinding(IsVisibleProperty, nameof(CheckInRow.ByText), converter: Resource<IValueConverter>("HasText"));
            row.Add(new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { Bound("BaseLabel", nameof(CheckInRow.Name)), by } }, 1);
            row.Add(Bound("Caption", nameof(CheckInRow.HowText)), 2);
            return new VerticalStackLayout { Children = { new BoxView { Style = Resource<Style>("Hairline") }, row } };
        }));
        return Section("Check-ins", "AdminOverviewCheckIns", new Border { Style = Resource<Style>("TablePanel"), Content = new VerticalStackLayout { Children = { bar, list } } });
    }

    static View Section(string title, string? id, View content) => new VerticalStackLayout { Spacing = 10, Children = { Text(title, "SectionTitle", id), content } };

    /// <summary>A see-through button over a strip figure or link row: the whole area opens the page, for mouse, keyboard and UI Automation.</summary>
    static Button Overlay(AdminOverviewCard card)
    {
        var open = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, CornerRadius = 0, AutomationId = "AdminOverviewOpen-" + card.Key };
        SemanticProperties.SetDescription(open, "Open " + card.Title);
        ToolTipProperties.SetText(open, "Open " + card.Title);
        open.Clicked += (_, _) => Open(card.Title);
        return open;
    }

    static Grid Tap(IconView icon, string id, string name, System.Windows.Input.ICommand command)
    {
        var button = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0, WidthRequest = 40, HeightRequest = 40, CornerRadius = 12, AutomationId = id, Command = command };
        SemanticProperties.SetDescription(button, name);
        ToolTipProperties.SetText(button, name);
        icon.HorizontalOptions = icon.VerticalOptions = LayoutOptions.Center;
        return new Grid { WidthRequest = 40, HeightRequest = 40, VerticalOptions = LayoutOptions.Center, Children = { button, icon } };
    }

    protected override bool LiveRecords => true;   // F4: payments and check-ins move its figures; its own "Checked" line says when

    static void Open(string title) => (Shell.Current as StaffShell)?.ShowAdminPage(title);
    static Label Bound(string style, string path) { var l = Text("", style); l.VerticalOptions = LayoutOptions.Center; l.SetBinding(Label.TextProperty, path); return l; }
    static Color Theme(string key) => (Color)Application.Current!.Resources[key];
    static T Resource<T>(string key) => (T)Application.Current!.Resources[key];
    static Label Text(string text, string style, string? id = null) => new() { Text = text, Style = Resource<Style>(style), AutomationId = id };
}
#endif
