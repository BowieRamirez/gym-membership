using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>
/// Line icons from Lucide (ISC licence, https://lucide.dev), as SVG path data on a 24 x 24 grid. They are drawn as vectors, like the header bell,
/// so they need no image files and follow the theme at once. They contain only absolute M, L, C, Q and Z commands, with every number
/// separated by a space, and no arcs: MAUI's own arc conversion produces NaN for shallow arcs and Windows then refuses to draw the shape
/// (error 0x88990011). IconTests runs every outline through MAUI's parser to keep it that way.
/// </summary>
public static class Icons
{
    public static readonly IReadOnlyDictionary<string, string> Data = new Dictionary<string, string>
    {
        ["log-in"] = "M 10 17 L 15 12 L 10 7 M 15 12 L 3 12 M 15 3 L 19 3 C 20.105 3 21 3.895 21 5 L 21 19 C 21 20.105 20.105 21 19 21 L 15 21",
        ["receipt"] = "M 12 17 L 12 7 M 16 8 L 10 8 C 8.895 8 8 8.895 8 10 C 8 11.105 8.895 12 10 12 L 14 12 C 15.105 12 16 12.895 16 14 C 16 15.105 15.105 16 14 16 L 8 16 M 4 3 C 4 2.448 4.448 2 5 2 C 5.248 1.999 5.49 2.068 5.7 2.2 L 6.633 2.8 C 7.06 3.073 7.606 3.073 8.033 2.8 L 8.967 2.2 C 9.394 1.927 9.94 1.927 10.367 2.2 L 11.3 2.8 C 11.727 3.073 12.273 3.073 12.7 2.8 L 13.633 2.2 C 14.06 1.927 14.606 1.927 15.033 2.2 L 15.967 2.8 C 16.394 3.073 16.94 3.073 17.367 2.8 L 18.3 2.2 C 18.51 2.068 18.752 1.999 19 2 C 19.552 2 20 2.448 20 3 L 20 21 C 20 21.552 19.552 22 19 22 C 18.752 22.001 18.51 21.932 18.3 21.8 L 17.367 21.2 C 16.94 20.927 16.394 20.927 15.967 21.2 L 15.033 21.8 C 14.606 22.073 14.06 22.073 13.633 21.8 L 12.7 21.2 C 12.273 20.927 11.727 20.927 11.3 21.2 L 10.367 21.8 C 9.94 22.073 9.394 22.073 8.967 21.8 L 8.033 21.2 C 7.606 20.927 7.06 20.927 6.633 21.2 L 5.7 21.8 C 5.49 21.932 5.248 22.001 5 22 C 4.448 22 4 21.552 4 21 Z",
        ["waves"] = "M 2 12 Q 4.5 14 7 12 Q 9.5 10 12 12 Q 14.5 14 17 12 Q 19.5 10 22 12 M 2 19 Q 4.5 21 7 19 Q 9.5 17 12 19 Q 14.5 21 17 19 Q 19.5 17 22 19 M 2 5 Q 4.5 7 7 5 Q 9.5 3 12 5 Q 14.5 7 17 5 Q 19.5 3 22 5",
        ["message-square"] = "M 22 17 C 22 18.105 21.105 19 20 19 L 6.828 19 C 6.298 19 5.789 19.211 5.414 19.586 L 3.212 21.788 C 3.009 21.991 2.704 22.052 2.438 21.942 C 2.173 21.832 2 21.573 2 21.286 L 2 5 C 2 3.895 2.895 3 4 3 L 20 3 C 21.105 3 22 3.895 22 5 Z",
        ["ticket"] = "M 2 9 C 3.657 9 5 10.343 5 12 C 5 13.657 3.657 15 2 15 L 2 17 C 2 18.105 2.895 19 4 19 L 20 19 C 21.105 19 22 18.105 22 17 L 22 15 C 20.343 15 19 13.657 19 12 C 19 10.343 20.343 9 22 9 L 22 7 C 22 5.895 21.105 5 20 5 L 4 5 C 2.895 5 2 5.895 2 7 Z M 13 5 L 13 7 M 13 17 L 13 19 M 13 11 L 13 13",
        ["refresh-cw"] = "M 3 12 C 3 7.029 7.029 3 12 3 C 14.516 3.009 16.931 3.991 18.74 5.74 L 21 8 M 21 3 L 21 8 L 16 8 M 21 12 C 21 16.971 16.971 21 12 21 C 9.484 20.991 7.069 20.009 5.26 18.26 L 3 16 M 8 16 L 3 16 L 3 21",
        ["footprints"] = "M 4 16 L 4 13.62 C 4 11.5 2.97 10.5 3 8 C 3.03 5.28 4.49 2 7.5 2 C 9.37 2 10 3.8 10 5.5 C 10 8.61 8 11.16 8 14.18 L 8 16 C 8 17.105 7.105 18 6 18 C 4.895 18 4 17.105 4 16 Z M 20 20 L 20 17.62 C 20 15.5 21.03 14.5 21 12 C 20.97 9.28 19.51 6 16.5 6 C 14.63 6 14 7.8 14 9.5 C 14 12.61 16 15.16 16 18.18 L 16 20 C 16 21.105 16.895 22 18 22 C 19.105 22 20 21.105 20 20 Z M 16 17 L 20 17 M 4 13 L 8 13",
        ["banknote"] = "M 4 6 L 20 6 C 21.105 6 22 6.895 22 8 L 22 16 C 22 17.105 21.105 18 20 18 L 4 18 C 2.895 18 2 17.105 2 16 L 2 8 C 2 6.895 2.895 6 4 6 Z M 10 12 C 10 13.105 10.895 14 12 14 C 13.105 14 14 13.105 14 12 C 14 10.895 13.105 10 12 10 C 10.895 10 10 10.895 10 12 Z M 6 12 L 6.01 12 M 18 12 L 18.01 12",
        ["smartphone"] = "M 7 2 L 17 2 C 18.105 2 19 2.895 19 4 L 19 20 C 19 21.105 18.105 22 17 22 L 7 22 C 5.895 22 5 21.105 5 20 L 5 4 C 5 2.895 5.895 2 7 2 Z M 12 18 L 12.01 18",
        ["check"] = "M 20 6 L 9 17 L 4 12",
        ["x"] = "M 18 6 L 6 18 M 6 6 L 18 18",
        ["chevron-left"] = "M 15 18 L 9 12 L 15 6",
        // eye and eye-off: drawn here with curves only (Lucide's own eye uses arcs)
        ["eye"] = "M 2 12 C 4.5 7.333 7.833 5 12 5 C 16.167 5 19.5 7.333 22 12 C 19.5 16.667 16.167 19 12 19 C 7.833 19 4.5 16.667 2 12 Z M 9 12 C 9 10.343 10.343 9 12 9 C 13.657 9 15 10.343 15 12 C 15 13.657 13.657 15 12 15 C 10.343 15 9 13.657 9 12 Z",
        ["eye-off"] = "M 2 12 C 4.5 7.333 7.833 5 12 5 C 16.167 5 19.5 7.333 22 12 C 19.5 16.667 16.167 19 12 19 C 7.833 19 4.5 16.667 2 12 Z M 9 12 C 9 10.343 10.343 9 12 9 C 13.657 9 15 10.343 15 12 C 15 13.657 13.657 15 12 15 C 10.343 15 9 13.657 9 12 Z M 3 3 L 21 21",
        ["chevron-right"] = "M 9 18 L 15 12 L 9 6",
        ["landmark"] = "M10 18 L 10 11 M 11.119 2.205 C 11.675 1.932 12.325 1.932 12.881 2.205 L 20.721 6.051 C 20.93 6.153 21.041 6.387 20.988 6.613 C 20.935 6.84 20.733 7 20.5 7 L 3.5 7 C 3.267 7 3.065 6.84 3.013 6.613 C 2.96 6.386 3.071 6.153 3.28 6.051 Z M 14 18 L 14 11 M 18 18 L 18 11 M 3 22 L 21 22 M 6 18 L 6 11",
    };
}

/// <summary>
/// One of the Icons, drawn in the theme's ink. OnInk flips it to the colour of text on a filled button, so it never vanishes on one.
/// It is decorative (the words beside it say the same) and lets touches through to whatever it sits on.
/// </summary>
public class IconView : ContentView
{
    public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(string), typeof(IconView), "",
        propertyChanged: (b, _, _) => ((IconView)b).Redraw());
    public static readonly BindableProperty OnInkProperty = BindableProperty.Create(nameof(OnInk), typeof(bool), typeof(IconView), false,
        propertyChanged: (b, _, _) => ((IconView)b).Recolour());

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool OnInk { get => (bool)GetValue(OnInkProperty); set => SetValue(OnInkProperty, value); }

    readonly Microsoft.Maui.Controls.Shapes.Path _path = new() { Aspect = Stretch.Uniform, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };

    public IconView()
    {
        WidthRequest = 22; HeightRequest = 22;
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
        Content = _path;
        Recolour();
    }

    void Redraw() =>
        _path.Data = Icons.Data.TryGetValue(Icon ?? "", out var d) ? new PathGeometryConverter().ConvertFromInvariantString(d) as Geometry : null;

    void Recolour()
    {
        static Color Pick(string key, Color fallback) => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : fallback;
        var light = OnInk ? Pick("White", Colors.White) : Pick("InkLight", Colors.Black);
        var dark = OnInk ? Pick("ChalkDark", Colors.Black) : Pick("InkDark", Colors.White);
        _path.SetAppTheme<Brush>(Shape.StrokeProperty, new SolidColorBrush(light), new SolidColorBrush(dark));
    }
}

/// <summary>
/// The eye inside a password box: tap to show what was typed, tap again to hide it. It is a real Button underneath (touch, keyboard,
/// screen reader: "Show password" / "Hide password") and puts the box back to hidden whenever the page shows again.
/// </summary>
public class EyeButton : Grid
{
    public static readonly BindableProperty EntryProperty = BindableProperty.Create(nameof(Entry), typeof(Entry), typeof(EyeButton), null,
        propertyChanged: (b, _, _) => ((EyeButton)b).Show(false));
    public Entry? Entry { get => (Entry?)GetValue(EntryProperty); set => SetValue(EntryProperty, value); }

    readonly IconView _icon = new() { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    readonly Button _button = new() { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0, WidthRequest = 40, MinimumHeightRequest = 40 };

    public EyeButton()
    {
        VerticalOptions = LayoutOptions.Center;
        _button.Clicked += (_, _) => Show(Entry?.IsPassword == true);
        Children.Add(_button);
        Children.Add(_icon);
        Loaded += (_, _) => Show(false);
    }

    void Show(bool visible)
    {
        if (Entry is not null) Entry.IsPassword = !visible;
        _icon.Icon = visible ? "eye-off" : "eye";
        SemanticProperties.SetDescription(_button, visible ? "Hide password" : "Show password");
    }
}

/// <summary>
/// A choice button with an icon on its left. It stays a real Button (touch, keyboard, screen reader); IsSelected fills it, flips the icon to
/// match, and is announced as "selected".
/// </summary>
public class IconButton : ContentView
{
    public static readonly BindableProperty TextProperty = Prop<string>(nameof(Text), "");
    public static readonly BindableProperty IconProperty = Prop<string>(nameof(Icon), "");
    public static readonly BindableProperty CommandProperty = Prop<ICommand?>(nameof(Command), null);
    public static readonly BindableProperty CommandParameterProperty = Prop<object?>(nameof(CommandParameter), null);
    public static readonly BindableProperty IsSelectedProperty = Prop(nameof(IsSelected), false);

    static BindableProperty Prop<T>(string name, T def) =>
        BindableProperty.Create(name, typeof(T), typeof(IconButton), def, propertyChanged: (b, _, _) => ((IconButton)b).Refresh());

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    readonly Button _button = new() { Padding = new Thickness(40, 0, 16, 0) };   // room on the left for the icon
    readonly IconView _icon = new() { HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(14, 0, 0, 0) };

    public IconButton()
    {
        Content = new Grid { Children = { _button, _icon } };
        Refresh();
    }

    void Refresh()
    {
        _button.Text = Text; _button.Command = Command; _button.CommandParameter = CommandParameter;
        _icon.Icon = Icon; _icon.OnInk = IsSelected;
        if (Application.Current!.Resources.TryGetValue(IsSelected ? "SmallButton" : "SmallOutlineButton", out var style)) _button.Style = (Style)style;
        SemanticProperties.SetDescription(_button, IsSelected ? $"{Text}, selected" : Text);
    }
}
