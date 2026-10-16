using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>
/// Line icons from Lucide (ISC licence, https://lucide.dev), as SVG path data on a 24 x 24 grid. They are drawn as vectors, like the header bell,
/// so they need no image files and follow the theme at once. Every number and command is separated by a space on purpose.
/// </summary>
public static class Icons
{
    public static readonly IReadOnlyDictionary<string, string> Data = new Dictionary<string, string>
    {
        ["log-in"] = "m 10 17 5 -5 -5 -5 M 15 12 H 3 M 15 3 h 4 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 h -4",
        ["receipt"] = "M 12 17 V 7 M 16 8 h -6 a 2 2 0 0 0 0 4 h 4 a 2 2 0 0 1 0 4 H 8 M 4 3 a 1 1 0 0 1 1 -1 1.3 1.3 0 0 1 .7 .2 l .933 .6 a 1.3 1.3 0 0 0 1.4 0 l .934 -.6 a 1.3 1.3 0 0 1 1.4 0 l .933 .6 a 1.3 1.3 0 0 0 1.4 0 l .933 -.6 a 1.3 1.3 0 0 1 1.4 0 l .934 .6 a 1.3 1.3 0 0 0 1.4 0 l .933 -.6 A 1.3 1.3 0 0 1 19 2 a 1 1 0 0 1 1 1 v 18 a 1 1 0 0 1 -1 1 1.3 1.3 0 0 1 -.7 -.2 l -.933 -.6 a 1.3 1.3 0 0 0 -1.4 0 l -.934 .6 a 1.3 1.3 0 0 1 -1.4 0 l -.933 -.6 a 1.3 1.3 0 0 0 -1.4 0 l -.933 .6 a 1.3 1.3 0 0 1 -1.4 0 l -.934 -.6 a 1.3 1.3 0 0 0 -1.4 0 l -.933 .6 a 1.3 1.3 0 0 1 -.7 .2 1 1 0 0 1 -1 -1 z",
        ["waves"] = "M 2 12 q 2.5 2 5 0 t 5 0 5 0 5 0 M 2 19 q 2.5 2 5 0 t 5 0 5 0 5 0 M 2 5 q 2.5 2 5 0 t 5 0 5 0 5 0",
        ["message-square"] = "M 22 17 a 2 2 0 0 1 -2 2 H 6.828 a 2 2 0 0 0 -1.414 .586 l -2.202 2.202 A .71 .71 0 0 1 2 21.286 V 5 a 2 2 0 0 1 2 -2 h 16 a 2 2 0 0 1 2 2 z",
        ["ticket"] = "M 2 9 a 3 3 0 0 1 0 6 v 2 a 2 2 0 0 0 2 2 h 16 a 2 2 0 0 0 2 -2 v -2 a 3 3 0 0 1 0 -6 V 7 a 2 2 0 0 0 -2 -2 H 4 a 2 2 0 0 0 -2 2 Z M 13 5 v 2 M 13 17 v 2 M 13 11 v 2",
        ["refresh-cw"] = "M 3 12 a 9 9 0 0 1 9 -9 9.75 9.75 0 0 1 6.74 2.74 L 21 8 M 21 3 v 5 h -5 M 21 12 a 9 9 0 0 1 -9 9 9.75 9.75 0 0 1 -6.74 -2.74 L 3 16 M 8 16 H 3 v 5",
        ["footprints"] = "M 4 16 v -2.38 C 4 11.5 2.97 10.5 3 8 c .03 -2.72 1.49 -6 4.5 -6 C 9.37 2 10 3.8 10 5.5 c 0 3.11 -2 5.66 -2 8.68 V 16 a 2 2 0 1 1 -4 0 Z M 20 20 v -2.38 c 0 -2.12 1.03 -3.12 1 -5.62 -.03 -2.72 -1.49 -6 -4.5 -6 C 14.63 6 14 7.8 14 9.5 c 0 3.11 2 5.66 2 8.68 V 20 a 2 2 0 1 0 4 0 Z M 16 17 h 4 M 4 13 h 4",
        ["banknote"] = "M 4 6 h 16 a 2 2 0 0 1 2 2 v 8 a 2 2 0 0 1 -2 2 h -16 a 2 2 0 0 1 -2 -2 v -8 a 2 2 0 0 1 2 -2 z M 10 12 a 2 2 0 1 0 4 0 a 2 2 0 1 0 -4 0 M 6 12 h .01 M 18 12 h .01",
        ["smartphone"] = "M 7 2 h 10 a 2 2 0 0 1 2 2 v 16 a 2 2 0 0 1 -2 2 h -10 a 2 2 0 0 1 -2 -2 v -16 a 2 2 0 0 1 2 -2 z M 12 18 h .01",
        ["landmark"] = "M 10 18 v -7 M 11.119 2.205 a 2 2 0 0 1 1.762 0 l 7.84 3.846 A .5 .5 0 0 1 20.5 7 h -17 a .5 .5 0 0 1 -.22 -.949 z M 14 18 v -7 M 18 18 v -7 M 3 22 h 18 M 6 18 v -7",
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
