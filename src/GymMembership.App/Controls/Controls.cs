using System.Globalization;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>
/// A row of upright pills: filled up to Fraction, dashed outlines after. The "days left" meter on the membership screen.
/// </summary>
public class LaneView : GraphicsView
{
    public static readonly BindableProperty FractionProperty = Prop(nameof(Fraction), 1d);
    public static readonly BindableProperty DashColorProperty = Prop(nameof(DashColor), Colors.Black);
    public static readonly BindableProperty TrackColorProperty = Prop(nameof(TrackColor), Colors.Gray);

    static BindableProperty Prop<T>(string name, T def) =>
        BindableProperty.Create(name, typeof(T), typeof(LaneView), def, propertyChanged: (b, _, _) => ((LaneView)b).Invalidate());

    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
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
    /// <summary>Columns is a most: a narrow window gets fewer, so no card is squeezed below this width.</summary>
    public double MinColumnWidth { get; set; } = 320;

    int Fit(double width)
    {
        var room = width - Padding.HorizontalThickness;
        var fit = double.IsFinite(room) ? (int)((room + Spacing) / (MinColumnWidth + Spacing)) : Columns;
        return Math.Clamp(fit, 1, Math.Max(1, Columns));
    }

    protected override Microsoft.Maui.Layouts.ILayoutManager CreateLayoutManager() => new Manager(this);

    sealed class Manager(CardGrid grid) : Microsoft.Maui.Layouts.ILayoutManager
    {
        List<IView> Visible() => grid.Where(v => v.Visibility != Visibility.Collapsed).ToList();
        double ColumnWidth(double width, int n) => Math.Max(0, (width - grid.Padding.HorizontalThickness - grid.Spacing * (n - 1)) / n);

        public Size Measure(double widthConstraint, double heightConstraint)
        {
            var n = grid.Fit(widthConstraint);
            var w = ColumnWidth(widthConstraint, n);
            var rows = Visible().Chunk(n).Select(r => r.Max(v => v.Measure(w, double.PositiveInfinity).Height)).ToList();
            var height = rows.Sum() + grid.Spacing * Math.Max(0, rows.Count - 1) + grid.Padding.VerticalThickness;
            return new Size(widthConstraint, height);
        }

        public Size ArrangeChildren(Rect bounds)
        {
            var n = grid.Fit(bounds.Width);
            var w = ColumnWidth(bounds.Width, n);
            var y = bounds.Top + grid.Padding.Top;
            foreach (var row in Visible().Chunk(n))
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
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AutomationId) && _label.AutomationId is null) _label.AutomationId = AutomationId; };
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

/// <summary>
/// A field's frame turns lime (dark theme) or Plate Blue (light) while the box inside it has focus: the keyboard-focus indicator for every
/// Entry, Editor and SearchBar in a "Field" border. MauiProgram attaches it to each input as its handler connects.
/// </summary>
public static class FieldFocus
{
    public static void Attach(InputView input)
    {
        input.Focused -= Changed; input.Unfocused -= Changed;
        input.Focused += Changed; input.Unfocused += Changed;
    }

    static void Changed(object? sender, FocusEventArgs e)
    {
        var field = Application.Current!.Resources["Field"];
        for (var p = (sender as Element)?.Parent; p is not null; p = p.Parent)
            if (p is Border b && b.Style == field) { if (e.IsFocused) Mark(b); else Unmark(b); return; }
    }

    // a plain value (not a theme binding) so ClearValue hands the frame straight back to the Field style
    public static void Mark(Border b) =>
        b.Stroke = Pick(Application.Current!.RequestedTheme == AppTheme.Dark ? "Lime" : "PlateBlueLight");

    public static void Unmark(Border b) => b.ClearValue(Border.StrokeProperty);

    internal static Color Pick(string key) => Application.Current!.Resources.TryGetValue(key, out var v) && v is Color c ? c : Colors.Gray;
}

/// <summary>
/// The 6-digit code from the email, shown as six boxes. One real Entry lies over them, almost invisible, so typing, pasting, backspace and
/// autofill work as in any text box; the boxes only show its digits and which box is next.
/// </summary>
public class CodeBoxes : Grid
{
    public static readonly BindableProperty CodeProperty = BindableProperty.Create(nameof(Code), typeof(string), typeof(CodeBoxes), "",
        BindingMode.TwoWay, propertyChanged: (b, _, _) => ((CodeBoxes)b).Show());
    public string? Code { get => (string?)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    readonly Entry _entry = new() { Keyboard = Keyboard.Numeric, MaxLength = 6, Opacity = 0.01, HeightRequest = 56 };
    readonly Border[] _boxes = new Border[6];
    readonly Label[] _digits = new Label[6];

    public CodeBoxes()
    {
        ColumnSpacing = 8;
        for (var i = 0; i < 6; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _digits[i] = new Label { FontFamily = "BarlowSemiBold", FontSize = 22, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            _boxes[i] = new Border { Style = (Style)Application.Current!.Resources["Field"], Padding = 0, HeightRequest = 56, Content = _digits[i] };
            AutomationProperties.SetIsInAccessibleTree(_boxes[i], false);
            this.Add(_boxes[i], i);
        }
        this.Add(_entry, 0, 0);
        SetColumnSpan((BindableObject)_entry, 6);
        SemanticProperties.SetDescription(_entry, "6-digit code");
        _entry.TextChanged += (_, e) =>
        {
            var digits = new string((e.NewTextValue ?? "").Where(char.IsAsciiDigit).Take(6).ToArray());
            if (digits != e.NewTextValue) _entry.Text = digits; else Code = digits;
        };
        _entry.Focused += (_, _) => Show();
        _entry.Unfocused += (_, _) => Show();
    }

    void Show()
    {
        var code = Code ?? "";
        if (_entry.Text != code) _entry.Text = code;
        for (var i = 0; i < 6; i++)
        {
            _digits[i].Text = i < code.Length ? code[i].ToString() : "";
            if (_entry.IsFocused && i == Math.Min(code.Length, 5)) FieldFocus.Mark(_boxes[i]); else FieldFocus.Unmark(_boxes[i]);
        }
    }
}

/// <summary>
/// Under a new password: four bars and a word for how strong it is, and the checklist from AccountRules. Only the length is required.
/// Colour is never the only signal: the word and the ticks say the same.
/// </summary>
public class PasswordStrength : VerticalStackLayout
{
    public static readonly BindableProperty PasswordProperty = BindableProperty.Create(nameof(Password), typeof(string), typeof(PasswordStrength), "",
        propertyChanged: (b, _, _) => ((PasswordStrength)b).Show());
    public string? Password { get => (string?)GetValue(PasswordProperty); set => SetValue(PasswordProperty, value); }

    static readonly string[] Words = ["", "Weak", "Fair", "Good", "Strong"];
    static readonly string[] Tones = ["", "PlateRed", "PlateYellow", "PlateGreen", "PlateGreen"];
    readonly BoxView[] _bars = new BoxView[4];
    readonly Label _word = new() { Style = (Style)Application.Current!.Resources["Caption"], HorizontalOptions = LayoutOptions.End };
    readonly Label[] _checks = new Label[3];
    static readonly string[] CheckText = ["6 to 64 characters", "At least one number", "Upper and lower case letters"];

    public PasswordStrength()
    {
        Spacing = 4;
        var bars = new Grid { ColumnSpacing = 4, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Auto)] };
        for (var i = 0; i < 4; i++) { _bars[i] = new BoxView { HeightRequest = 4, CornerRadius = 2, VerticalOptions = LayoutOptions.Center }; bars.Add(_bars[i], i); }
        bars.Add(_word, 4);
        Add(bars);
        for (var i = 0; i < 3; i++) Add(_checks[i] = new Label { Style = (Style)Application.Current!.Resources["Caption"] });
        Show();
    }

    void Show()
    {
        var password = Password ?? "";
        var score = AccountRules.PasswordStrength(password);
        var (length, number, mixed) = AccountRules.PasswordChecks(password);
        for (var i = 0; i < 4; i++)
            if (i < score) _bars[i].SetAppThemeColor(BoxView.ColorProperty, FieldFocus.Pick(Tones[score] + "Light"), FieldFocus.Pick(Tones[score] + "Dark"));
            else _bars[i].SetAppThemeColor(BoxView.ColorProperty, FieldFocus.Pick("LineLight"), FieldFocus.Pick("LineDark"));
        _word.Text = Words[score];
        bool[] ok = [length, number, mixed];
        for (var i = 0; i < 3; i++) _checks[i].Text = (ok[i] ? "✓  " : "–  ") + CheckText[i];
    }
}
