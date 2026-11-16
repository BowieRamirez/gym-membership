using System.Windows.Input;

namespace GymMembership.App.Controls;

/// <summary>A pick-one chip (Messages Coach / Gym, Revenue's period): the chosen one is yellow with black text.
/// Owner, 2026-11-16: on Windows a Button whose own colour changes after a real mouse click keeps showing the colour of its
/// pressed/hover state, so clicking one chip left the other yellow too. Here the colour is on a Border behind a clear Button
/// (as Book an amenity's pay options): the Button only takes the click, the keyboard and the screen reader, and its own colour
/// never changes.</summary>
public class ChoiceChip : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(nameof(Text), typeof(string), typeof(ChoiceChip), "",
        propertyChanged: (b, _, _) => ((ChoiceChip)b).Update());
    public static readonly BindableProperty IsChosenProperty = BindableProperty.Create(nameof(IsChosen), typeof(bool), typeof(ChoiceChip), false,
        propertyChanged: (b, _, _) => ((ChoiceChip)b).Update());
    public static readonly BindableProperty CommandProperty = BindableProperty.Create(nameof(Command), typeof(ICommand), typeof(ChoiceChip),
        propertyChanged: (b, _, n) => ((ChoiceChip)b)._button.Command = n as ICommand);
    public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(nameof(CommandParameter), typeof(object), typeof(ChoiceChip),
        propertyChanged: (b, _, n) => ((ChoiceChip)b)._button.CommandParameter = n);

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsChosen { get => (bool)GetValue(IsChosenProperty); set => SetValue(IsChosenProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    readonly Border _box;
    readonly Label _label = new() { FontFamily = "BarlowMedium", FontSize = 14, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.Center };
    // see-through by opacity, not a colour: a Transparent Button on Windows falls back to the default grey button and hid the label
    // (owner, 2026-11-16). At 0.01 it still takes the click, the keyboard focus and the screen reader.
    readonly Button _button = new() { Text = "", Opacity = 0.01, BorderWidth = 0, Padding = 0, MinimumHeightRequest = 0, MinimumWidthRequest = 0 };

    public ChoiceChip()
    {
        var height = DeviceInfo.Platform == DevicePlatform.WinUI ? 34 : 48;
        HorizontalOptions = LayoutOptions.Start;
        _box = new Border
        {
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            StrokeThickness = 1, Padding = new Thickness(14, 0), HeightRequest = height, MinimumHeightRequest = height,
            Content = _label,
        };
        Content = new Grid { Children = { _box, _button } };   // the clear button lies over the whole chip
        Update();
    }

    void Update()
    {
        _label.Text = Text;
        if (IsChosen)
        {
            _box.SetAppThemeColor(BackgroundColorProperty, Theme("ChosenLight"), Theme("Signal"));
            _box.SetAppThemeColor(Border.StrokeProperty, Theme("ChosenLight"), Theme("Signal"));
            _label.SetAppThemeColor(Label.TextColorProperty, Theme("OnChosenLight"), Theme("Black"));
        }
        else
        {
            _box.BackgroundColor = Colors.Transparent;
            _box.SetAppThemeColor(Border.StrokeProperty, Theme("EdgeLight"), Theme("EdgeDark"));
            _label.SetAppThemeColor(Label.TextColorProperty, Theme("InkLight"), Theme("InkDark"));
        }
        SemanticProperties.SetDescription(_button, IsChosen ? $"{Text}, selected" : Text);
    }

    // ChosenLight and OnChosenLight differ on a phone and a PC (Colors.xaml OnIdiom)
    static Color Theme(string key) => Application.Current?.Resources.TryGetValue(key, out var v) != true ? Colors.Gray : v switch
    {
        Color c => c,
        OnIdiom<Color> o => DeviceInfo.Idiom == DeviceIdiom.Phone ? o.Phone : o.Default,
        _ => Colors.Gray,
    };
}
