using System.Globalization;
using GymMembership.Core.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>
/// The court-floor lane marking: a dashed line. Dashes up to Fraction are solid, the rest are faded.
/// Used as a divider (Fraction=1) and as the "days left" meter on the membership screen.
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
            var t = (float)v.Thickness;
            float dash = t * 3.5f, gap = t * 2.5f, y = rect.Height / 2;
            var solidUntil = rect.Width * (float)Math.Clamp(v.Fraction, 0, 1);
            canvas.StrokeSize = t;
            canvas.StrokeLineCap = LineCap.Butt;
            for (var x = 0f; x < rect.Width; x += dash + gap)
            {
                canvas.StrokeColor = x < solidUntil ? v.DashColor : v.TrackColor;
                canvas.DrawLine(x, y, Math.Min(x + dash, rect.Width), y);
            }
        }
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

    static readonly HashSet<string> Good = ["verified", "active", "completed", "approved", "present"];
    static readonly HashSet<string> Bad = ["rejected", "expired", "cancelled", "absent", "no_show", "inactive"];
    static readonly HashSet<string> Wait = ["pending", "late"];

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
