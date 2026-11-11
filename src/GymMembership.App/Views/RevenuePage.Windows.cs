#if WINDOWS
using System.Collections.Specialized;
using GymMembership.App.Controls;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

/// <summary>
/// The Windows Revenue page (owner 2026-11-12, approved mockup docs/mockups/revenue.html): today, this week, this month and check-ins a
/// day; the period; a day-by-day chart; where the money came from; a day-by-day table. Android keeps the simpler page in the XAML.
/// Every part folds for a narrow window: the figures go two by two, the two breakdowns stack, the table drops two columns.
/// </summary>
public partial class RevenuePage
{
    View BuildDashboard(RevenueViewModel vm)
    {
        var body = new VerticalStackLayout { Spacing = 26 };
        body.Children.Add(FigureStrip(vm));
        body.Children.Add(Period(vm));
        body.Children.Add(ChartPanel(vm));
        body.Children.Add(Section("Where it came from", Sources(vm)));
        body.Children.Add(Section("Day by day", DayTable(vm)));
        return body;
    }

    // ---------- the four figures: one panel split by 1 px lines (the grid's colour shows through), four across or two by two ----------
    static View FigureStrip(RevenueViewModel vm)
    {
        var strip = new Grid { ColumnSpacing = 1, RowSpacing = 1, AutomationId = "RevenueFigures" };
        strip.SetAppThemeColor(BackgroundColorProperty, Theme("LineLight"), Theme("LineDark"));
        var columns = 0;
        void Arrange()
        {
            var next = strip.Width >= 820 ? 4 : 2;
            if (strip.Width <= 0 || (next == columns && strip.Children.Count == vm.Figures.Count)) return;
            columns = next;
            strip.Children.Clear(); strip.ColumnDefinitions.Clear(); strip.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) strip.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (var i = 0; i < (vm.Figures.Count + columns - 1) / columns; i++) strip.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < vm.Figures.Count; i++) strip.Add(Figure(vm.Figures[i]), i % columns, i / columns);
        }
        strip.SizeChanged += (_, _) => Arrange();
        vm.Figures.CollectionChanged += (_, _) => { columns = 0; Arrange(); };
        return new Border { Style = Resource<Style>("TablePanel"), Content = strip };
    }

    static View Figure(RevenueFigure f)
    {
        var compare = Text(f.Compare, "BaseLabel");
        compare.FontFamily = "BarlowMedium";
        if (f.IsUp) compare.SetAppThemeColor(Label.TextColorProperty, Theme("GoodLight"), Theme("GoodDark"));
        else if (f.IsDown) compare.SetAppThemeColor(Label.TextColorProperty, Theme("BadLight"), Theme("BadDark"));
        else compare.SetAppThemeColor(Label.TextColorProperty, Theme("MutedLight"), Theme("MutedDark"));
        var value = Text(f.Value, "KpiValue", "RevenueFigure-" + f.Label);
        value.FontSize = 40;
        value.Margin = new Thickness(0, 4, 0, 2);
        var cell = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(20, 16, 20, 18), Children = { Text(f.Label, "Muted"), value, compare, Text(f.Sub, "Muted") } };
        cell.SetAppThemeColor(BackgroundColorProperty, Theme("CardLight"), Theme("CardDark"));
        SemanticProperties.SetDescription(cell, $"{f.Label}: {f.Value}. {f.Compare}. {f.Sub}");
        return cell;
    }

    // ---------- the period: three quick choices, or exact dates; the PDF follows what is shown ----------
    View Period(RevenueViewModel vm)
    {
        var head = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        var title = Text("Period", "SectionTitle"); title.VerticalOptions = LayoutOptions.Center; title.Margin = 0;
        head.Add(title);
        var pdf = new Button { Text = "Export PDF", Style = Resource<Style>("SmallOutlineButton"), Command = vm.ExportPdfCommand, AutomationId = "RevenueExportPdf" };
        SemanticProperties.SetHint(pdf, "Creates a PDF of the receipts and refunds shown and opens the share sheet");
        head.Add(pdf, 1);

        var row = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center };
        foreach (var (days, label) in new[] { ("7", "7 days"), ("30", "30 days"), ("90", "3 months") })
        {
            var chip = Chip(label, vm.ShowRangeCommand, days, "RevenueRange-" + days);
            chip.Margin = new Thickness(0, 0, 8, 8);
            void Mark() => Pressed(chip, vm.RangeDays.ToString() == days);
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.RangeDays)) Mark(); };
            Mark();
            row.Children.Add(chip);
        }
        View Date(string path, string name)
        {
            var picker = new DatePicker { MaximumDate = DateTime.Today.AddDays(1) };
            picker.SetBinding(DatePicker.DateProperty, path);
            SemanticProperties.SetDescription(picker, name);
            return new Border { Style = Resource<Style>("Field"), Content = picker, Margin = new Thickness(0, 0, 8, 8) };
        }
        // the two dates wrap as one piece, so "to" never ends a line
        row.Children.Add(new HorizontalStackLayout { Margin = new Thickness(8, 0, 0, 0), Children = { Date(nameof(vm.From), "From"),
            new Label { Text = "to", Style = Resource<Style>("Muted"), VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 8, 8) }, Date(nameof(vm.To), "To") } });
        row.Children.Add(new Button { Text = "Show these dates", Style = Resource<Style>("SmallOutlineButton"), Command = vm.LoadCommand, Margin = new Thickness(0, 0, 0, 8), AutomationId = "RevenueShowDates" });
        return new VerticalStackLayout { Spacing = 10, Children = { head, row } };
    }

    // ---------- the chart: the period's total, a Revenue / Check-ins switch, a column a day ----------
    static View ChartPanel(RevenueViewModel vm)
    {
        var total = Text("", "Numeral", "RevenueChartTotal");
        total.FontSize = 32;
        total.SetBinding(Label.TextProperty, nameof(vm.ChartTotalText));
        var sub = Text("", "Muted");
        sub.VerticalOptions = LayoutOptions.End; sub.Margin = new Thickness(0, 0, 0, 5);
        sub.SetBinding(Label.TextProperty, nameof(vm.ChartSubText));
        var words = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.End };
        total.Margin = new Thickness(0, 0, 14, 0);
        words.Children.Add(total); words.Children.Add(sub);

        var revenue = Chip("Revenue", vm.ShowMetricCommand, "revenue", "RevenueMetric-revenue");
        var visits = Chip("Check-ins", vm.ShowMetricCommand, "visits", "RevenueMetric-visits");
        var chart = new RevenueChart { AutomationId = "RevenueChart" };
        void Refresh() { Pressed(revenue, vm.Metric == "revenue"); Pressed(visits, vm.Metric == "visits"); chart.Show(vm.Chart, vm.Metric == "visits"); }
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(vm.Chart) or nameof(vm.Metric)) Refresh(); };
        Refresh();

        var top = new Grid { ColumnSpacing = 12, RowSpacing = 10, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        top.Add(words);
        top.Add(new HorizontalStackLayout { Spacing = 8, VerticalOptions = LayoutOptions.Start, Children = { revenue, visits } }, 1);
        return new Border { Style = Resource<Style>("TablePanel"), Padding = new Thickness(18, 16, 18, 12),
            Content = new VerticalStackLayout { Spacing = 14, Children = { top, chart } } };
    }

    // ---------- where it came from: by item and by method, side by side or stacked ----------
    static View Sources(RevenueViewModel vm)
    {
        var item = Shares("By item", vm.ByItem, Text("Refunds are taken off the item they paid for.", "Muted"));
        var foot = Text("", "Muted");
        foot.SetBinding(Label.TextProperty, nameof(vm.MethodsFoot));
        var method = Shares("By method", vm.ByMethod, foot);
        var grid = new Grid { ColumnSpacing = 1, RowSpacing = 1 };
        grid.SetAppThemeColor(BackgroundColorProperty, Theme("LineLight"), Theme("LineDark"));
        grid.Add(item); grid.Add(method);
        bool? wide = null;
        grid.SizeChanged += (_, _) =>
        {
            var now = grid.Width >= 760;
            if (grid.Width <= 0 || now == wide) return;
            wide = now;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            if (now) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            else { grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }
            grid.SetColumn(method, now ? 1 : 0); grid.SetRow(method, now ? 0 : 1);
        };
        return new Border { Style = Resource<Style>("TablePanel"), Content = grid };
    }

    static View Shares(string title, System.Collections.ObjectModel.ObservableCollection<RevenueShare> shares, View foot)
    {
        var rows = new VerticalStackLayout { Spacing = 2 };
        BindableLayout.SetItemsSource(rows, shares);
        BindableLayout.SetEmptyView(rows, Text("Nothing in this period.", "Muted"));
        BindableLayout.SetItemTemplate(rows, new DataTemplate(() =>
        {
            var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6), ColumnDefinitions = { new(100), new(GridLength.Star), new(GridLength.Auto), new(44) } };
            var name = Text("", "BaseLabel"); name.FontFamily = "BarlowMedium"; name.VerticalOptions = LayoutOptions.Center;
            name.SetBinding(Label.TextProperty, nameof(RevenueShare.Name));
            row.Add(name);
            // the bar: a track with its share filled in ink
            var fill = new BoxView { CornerRadius = 5 };
            fill.SetBinding(AbsoluteLayout.LayoutBoundsProperty, nameof(RevenueShare.Fraction), converter: new FractionBounds());
            AbsoluteLayout.SetLayoutFlags(fill, Microsoft.Maui.Layouts.AbsoluteLayoutFlags.All);
            fill.SetAppThemeColor(BoxView.ColorProperty, Theme("InkLight"), Theme("InkDark"));
            var track = new AbsoluteLayout { HeightRequest = 10, VerticalOptions = LayoutOptions.Center, Children = { fill } };
            track.SetAppThemeColor(BackgroundColorProperty, Theme("SurfaceLight"), Theme("SurfaceDark"));
            row.Add(new Border { StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 }, Content = track, VerticalOptions = LayoutOptions.Center }, 1);
            var amount = Text("", "BaseLabel"); amount.HorizontalOptions = LayoutOptions.End; amount.VerticalOptions = LayoutOptions.Center;
            amount.SetBinding(Label.TextProperty, nameof(RevenueShare.AmountText));
            row.Add(amount, 2);
            var pct = Text("", "Muted"); pct.HorizontalOptions = LayoutOptions.End; pct.VerticalOptions = LayoutOptions.Center;
            pct.SetBinding(Label.TextProperty, nameof(RevenueShare.PercentText));
            row.Add(pct, 3);
            return row;
        }));
        foot.Margin = new Thickness(0, 6, 0, 0);
        var cell = new VerticalStackLayout { Spacing = 6, Padding = new Thickness(18, 14, 18, 16), Children = { Text(title, "Heading"), rows, foot } };
        cell.SetAppThemeColor(BackgroundColorProperty, Theme("CardLight"), Theme("CardDark"));
        return cell;
    }

    sealed class FractionBounds : IValueConverter
    {
        public object Convert(object? value, Type t, object? p, System.Globalization.CultureInfo c) => new Rect(0, 0, Math.Clamp(value is double d ? d : 0, 0, 1), 1);
        public object ConvertBack(object? value, Type t, object? p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    // ---------- day by day: newest first, 7 at a time; a narrow window drops Check-ins and Refunds ----------
    static View DayTable(RevenueViewModel vm)
    {
        var narrow = false;
        ColumnDefinitionCollection Columns() => narrow
            ? [new(GridLength.Star), new(90), new(0), new(0), new(110)]
            : [new(GridLength.Star), new(110), new(110), new(110), new(130)];
        var head = new Grid { Style = Resource<Style>("TableHead"), ColumnDefinitions = Columns() };
        string[] titles = ["Day", "Payments", "Check-ins", "Refunds", "Net"];
        for (var i = 0; i < titles.Length; i++)
        {
            var t = Text(titles[i], "Caption");
            if (i > 0) t.HorizontalOptions = LayoutOptions.End;
            head.Add(t, i);
        }
        var rows = new VerticalStackLayout();
        BindableLayout.SetItemsSource(rows, vm.DayRows);
        BindableLayout.SetEmptyView(rows, new Label { Text = "No days in this period.", Style = Resource<Style>("Muted"), Margin = new Thickness(14) });
        BindableLayout.SetItemTemplate(rows, new DataTemplate(() =>
        {
            var row = new Grid { Style = Resource<Style>("TableRow"), Padding = new Thickness(14, 10), ColumnDefinitions = Columns() };
            var day = Text("", "BaseLabel"); day.FontFamily = "BarlowMedium";
            day.SetBinding(Label.TextProperty, new Binding(nameof(RevenueDayRow.Day), stringFormat: "{0:ddd d MMM}"));
            var cap = Text("", "Caption");
            cap.SetBinding(Label.TextProperty, nameof(RevenueDayRow.Caption));
            row.Add(new VerticalStackLayout { Spacing = 2, Children = { day, cap } });
            row.Add(Right(nameof(RevenueDayRow.Payments), null), 1);
            row.Add(Right(nameof(RevenueDayRow.Visits), null), 2);
            var refunds = Right(nameof(RevenueDayRow.RefundsText), null);
            refunds.BindingContextChanged += (_, _) =>
            {
                var bad = refunds.BindingContext is RevenueDayRow { HasRefunds: true };
                refunds.SetAppThemeColor(Label.TextColorProperty, Theme(bad ? "BadLight" : "MutedLight"), Theme(bad ? "BadDark" : "MutedDark"));
            };
            row.Add(refunds, 3);
            var net = Right(nameof(RevenueDayRow.Net), "₱{0:N0}"); net.FontFamily = "BarlowSemiBold";
            row.Add(net, 4);
            return new VerticalStackLayout { Children = { new BoxView { Style = Resource<Style>("Hairline") }, row } };
        }));
        var more = new Button { Style = Resource<Style>("SmallOutlineButton"), Command = vm.ToggleAllDaysCommand, HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(14, 10), AutomationId = "RevenueAllDays" };
        more.SetBinding(Button.TextProperty, nameof(vm.MoreDaysText));
        more.SetBinding(IsVisibleProperty, nameof(vm.HasMoreDays));
        var table = new VerticalStackLayout { Children = { head, rows, more } };
        table.SizeChanged += (_, _) =>
        {
            var now = table.Width > 0 && table.Width < 640;
            if (now == narrow) return;
            narrow = now;
            foreach (var g in new[] { head }.Concat(rows.Children.OfType<VerticalStackLayout>().Select(r => (Grid)r.Children[1])))
            {
                g.ColumnDefinitions = Columns();
                for (var i = 2; i <= 3 && i < g.Children.Count; i++) ((View)g.Children[i]).IsVisible = !narrow;
            }
        };
        rows.ChildAdded += (_, e) =>
        {
            if (e.Element is not VerticalStackLayout r || r.Children[1] is not Grid g) return;
            g.ColumnDefinitions = Columns();
            for (var i = 2; i <= 3; i++) ((View)g.Children[i]).IsVisible = !narrow;
        };
        return new Border { Style = Resource<Style>("TablePanel"), Content = table };
    }

    static Label Right(string path, string? format)
    {
        var l = Text("", "BaseLabel");
        l.HorizontalOptions = LayoutOptions.End;
        l.SetBinding(Label.TextProperty, new Binding(path, stringFormat: format));
        return l;
    }

    static Button Chip(string text, System.Windows.Input.ICommand command, string parameter, string id) =>
        new() { Text = text, Style = Resource<Style>("FilterChip"), Command = command, CommandParameter = parameter, AutomationId = id };

    // the chosen chip is yellow with black text, as the chips elsewhere in the app
    static void Pressed(Button chip, bool on)
    {
        if (on) { chip.BackgroundColor = Theme("Signal"); chip.TextColor = Theme("Black"); chip.BorderColor = Theme("Signal"); }
        else { chip.ClearValue(Button.BackgroundColorProperty); chip.ClearValue(Button.TextColorProperty); chip.ClearValue(Button.BorderColorProperty); }
        SemanticProperties.SetDescription(chip, on ? chip.Text + ", chosen" : chip.Text);
    }

    static View Section(string title, View content) => new VerticalStackLayout { Spacing = 10, Children = { Text(title, "SectionTitle"), content } };
    static Color Theme(string key) => (Color)Application.Current!.Resources[key];
    static T Resource<T>(string key) => (T)Application.Current!.Resources[key];
    static Label Text(string text, string style, string? id = null) => new() { Text = text, Style = Resource<Style>(style), AutomationId = id };
}
#endif
