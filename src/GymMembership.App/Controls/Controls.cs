using System.Globalization;
using GymMembership.Core.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>
/// A row of upright pills: filled up to Fraction, dashed outlines after. The "days left" meter on the membership screen.
/// </summary>
public class LaneView : GraphicsView
{
    public static readonly BindableProperty FractionProperty = Prop(nameof(Fraction), 1d);
    public static readonly BindableProperty ThicknessProperty = Prop(nameof(Thickness), 4d);
    public static readonly BindableProperty DashColorProperty = Prop(nameof(DashColor), Colors.Black);
    public static readonly BindableProperty TrackColorProperty = Prop(nameof(TrackColor), Colors.Gray);

    static BindableProperty Prop<T>(string name, T def) =>
        BindableProperty.Create(name, typeof(T), typeof(LaneView), def, propertyChanged: (b, _, _) => ((LaneView)b).Invalidate());

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Color DashColor { get => (Color)GetValue(DashColorProperty); set => SetValue(DashColorProperty, value); }
    public Color TrackColor { get => (Color)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }

    public LaneView()
    {
        Drawable = new LaneDrawable(this);
        HeightRequest = 12;
        HorizontalOptions = LayoutOptions.Fill;
        SemanticProperties.SetDescription(this, "Lane marking");
    }

    sealed class LaneDrawable(LaneView v) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF rect)
        {
            float h = rect.Height - 2, w = h * 0.62f, gap = 6;
            var count = Math.Max(1, (int)((rect.Width + gap) / (w + gap)));
            var filled = (int)Math.Round(count * Math.Clamp(v.Fraction, 0, 1));
            canvas.StrokeSize = 1.5f;
            canvas.StrokeDashPattern = [4, 3];
            canvas.StrokeColor = v.TrackColor;
            canvas.FillColor = v.DashColor;
            for (var i = 0; i < count; i++)
            {
                var x = i * (w + gap) + 1;
                if (i < filled) canvas.FillRoundedRectangle(x, 1, w, h, w / 2);
                else canvas.DrawRoundedRectangle(x, 1, w, h, w / 2);
            }
        }
    }
}

/// <summary>
/// Cards in equal columns, filled row by row; each row is as tall as its tallest card. Unlike FlexLayout it gives every card a fixed
/// width before measuring, so long text wraps inside the card instead of running past it. Works with BindableLayout.
/// </summary>
public class CardGrid : Layout
{
    public static readonly BindableProperty ColumnsProperty = BindableProperty.Create(nameof(Columns), typeof(int), typeof(CardGrid), 2,
        propertyChanged: (b, _, _) => ((CardGrid)b).InvalidateMeasure());
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double Spacing { get; set; } = 12;

    protected override Microsoft.Maui.Layouts.ILayoutManager CreateLayoutManager() => new Manager(this);

    sealed class Manager(CardGrid grid) : Microsoft.Maui.Layouts.ILayoutManager
    {
        List<IView> Visible() => grid.Where(v => v.Visibility != Visibility.Collapsed).ToList();
        double ColumnWidth(double width) { var n = Math.Max(1, grid.Columns); return Math.Max(0, (width - grid.Padding.HorizontalThickness - grid.Spacing * (n - 1)) / n); }

        public Size Measure(double widthConstraint, double heightConstraint)
        {
            var w = ColumnWidth(widthConstraint);
            var rows = Visible().Chunk(Math.Max(1, grid.Columns)).Select(r => r.Max(v => v.Measure(w, double.PositiveInfinity).Height)).ToList();
            var height = rows.Sum() + grid.Spacing * Math.Max(0, rows.Count - 1) + grid.Padding.VerticalThickness;
            return new Size(widthConstraint, height);
        }

        public Size ArrangeChildren(Rect bounds)
        {
            var w = ColumnWidth(bounds.Width);
            var y = bounds.Top + grid.Padding.Top;
            foreach (var row in Visible().Chunk(Math.Max(1, grid.Columns)))
            {
                var h = row.Max(v => v.DesiredSize.Height);
                for (var i = 0; i < row.Length; i++)
                    row[i].Arrange(new Rect(bounds.Left + grid.Padding.Left + i * (w + grid.Spacing), y, w, h));
                y += h + grid.Spacing;
            }
            return bounds.Size;
        }
    }
}

/// <summary>A card's header: one of the Icons on its chip, then the title. TextColor is for lime cards, which keep black words.</summary>
public class CardTitle : ContentView
{
    public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(string), typeof(CardTitle), "",
        propertyChanged: (b, _, n) => ((CardTitle)b)._icon.Icon = (string)n);
    public static readonly BindableProperty TextProperty = BindableProperty.Create(nameof(Text), typeof(string), typeof(CardTitle), "",
        propertyChanged: (b, _, n) => ((CardTitle)b)._label.Text = (string)n);
    public static readonly BindableProperty TextColorProperty = BindableProperty.Create(nameof(TextColor), typeof(Color), typeof(CardTitle), null,
        propertyChanged: (b, _, n) => { if (n is Color c) ((CardTitle)b)._label.TextColor = c; });

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Color? TextColor { get => (Color?)GetValue(TextColorProperty); set => SetValue(TextColorProperty, value); }

    readonly IconView _icon = new() { Size = 17, ChipSize = 34, VerticalOptions = LayoutOptions.Center };
    readonly Label _label = new() { FontFamily = "BarlowSemiBold", FontSize = 18, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.WordWrap };

    public CardTitle()
    {
        if (Application.Current?.Resources.TryGetValue("Heading", out var style) == true) _label.Style = (Style)style;
        _label.FontSize = 18;
        SemanticProperties.SetHeadingLevel(_label, SemanticHeadingLevel.Level2);
        Content = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 10, Children = { _icon, _label } };
        Grid.SetColumn(_label, 1);
    }
}

/// <summary>A status as a coloured dot plus a word, so colour is never the only signal.</summary>
public class StatusChip : ContentView
{
    public static readonly BindableProperty StatusProperty = BindableProperty.Create(nameof(Status), typeof(string), typeof(StatusChip),
        propertyChanged: (b, _, n) => ((StatusChip)b).Update(n as string));

    public string? Status { get => (string?)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    readonly Ellipse _dot = new();
    readonly Label _label = new() { VerticalOptions = LayoutOptions.Center, FontSize = 13 };

    static readonly HashSet<string> Good = ["verified", "active", "completed", "approved", "present", "available"];
    static readonly HashSet<string> Bad = ["rejected", "expired", "cancelled", "absent", "no_show", "inactive", "unavailable"];
    static readonly HashSet<string> Wait = ["pending", "late", "in_use"];

    public StatusChip()
    {
        HorizontalOptions = LayoutOptions.Start;
        var chip = new Border { Content = new HorizontalStackLayout { Spacing = 6, Children = { _dot, _label } } };
        if (Application.Current!.Resources.TryGetValue("Chip", out var style)) chip.Style = (Style)style;
        Content = chip;
    }

    void Update(string? status)
    {
        status ??= "";
        var key = Good.Contains(status) ? "DotVerified" : Bad.Contains(status) ? "DotRejected" : Wait.Contains(status) ? "DotPending" : "DotScheduled";
        if (Application.Current!.Resources.TryGetValue(key, out var style)) _dot.Style = (Style)style;
        var text = status.Replace('_', ' ');
        _label.Text = text.Length == 0 ? "" : char.ToUpper(text[0]) + text[1..];
        SemanticProperties.SetDescription(this, $"Status: {_label.Text}");
    }
}

/// <summary>Inline message under the page top: red edge for errors, green edge for confirmations.</summary>
public class Banner : ContentView
{
    public static readonly BindableProperty MessageProperty = BindableProperty.Create(nameof(Message), typeof(string), typeof(Banner),
        propertyChanged: (b, _, n) => ((Banner)b).IsVisible = !string.IsNullOrEmpty(n as string));
    public static readonly BindableProperty KindProperty = BindableProperty.Create(nameof(Kind), typeof(string), typeof(Banner), "error",
        propertyChanged: (b, _, _) => ((Banner)b).Restyle());

    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    readonly Border _box = new();

    public Banner()
    {
        IsVisible = false;
        var label = new Label();
        label.SetBinding(Label.TextProperty, new Binding(nameof(Message), source: this));
        _box.Content = label;
        Content = _box;
        SemanticProperties.SetDescription(this, "Message");
        Restyle();
    }

    void Restyle()
    {
        var key = Kind == "notice" ? "NoticePanel" : "ErrorPanel";
        if (Application.Current!.Resources.TryGetValue(key, out var style)) _box.Style = (Style)style;
    }
}

public class LocalDateConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return "";
        if (dt.Kind == DateTimeKind.Unspecified) dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return dt.ToLocalTime().ToString(parameter as string ?? "ddd d MMM, h:mm tt", culture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
