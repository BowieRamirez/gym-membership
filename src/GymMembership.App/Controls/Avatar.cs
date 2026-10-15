using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>A round profile picture. The initials sit underneath, so they show while the picture loads and whenever it cannot.</summary>
public class Avatar : ContentView
{
    public static readonly BindableProperty LettersProperty = BindableProperty.Create(nameof(Letters), typeof(string), typeof(Avatar), "?",
        propertyChanged: (b, _, n) => ((Avatar)b)._letters.Text = n as string);
    public static readonly BindableProperty UrlProperty = BindableProperty.Create(nameof(Url), typeof(string), typeof(Avatar),
        propertyChanged: (b, _, n) => ((Avatar)b)._photo.Source = Uri.TryCreate(n as string, UriKind.Absolute, out var uri) ? ImageSource.FromUri(uri) : null);
    public static readonly BindableProperty DiameterProperty = BindableProperty.Create(nameof(Diameter), typeof(double), typeof(Avatar), 40d,
        propertyChanged: (b, _, _) => ((Avatar)b).Resize());

    public string? Letters { get => (string?)GetValue(LettersProperty); set => SetValue(LettersProperty, value); }
    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    readonly Label _letters = new() { Text = "?", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, FontFamily = "BarlowSemiBold" };
    readonly Image _photo = new() { Aspect = Aspect.AspectFill };
    readonly Border _frame = new() { StrokeThickness = 0, Padding = 0 };

    public Avatar()
    {
        _frame.Content = new Grid { Children = { _letters, _photo } };
        _frame.SetAppThemeColor(Border.BackgroundColorProperty, Res("InkLight", Colors.Black), Res("InkDark", Colors.White));
        _letters.SetAppThemeColor(Label.TextColorProperty, Res("White", Colors.White), Res("ChalkDark", Colors.Black));
        Content = _frame;
        HorizontalOptions = LayoutOptions.Start;
        SemanticProperties.SetDescription(this, "Profile picture");
        Resize();
    }

    void Resize()
    {
        _frame.WidthRequest = _frame.HeightRequest = Diameter;
        _frame.StrokeShape = new RoundRectangle { CornerRadius = Diameter / 2 };
        _letters.FontSize = Diameter * 0.4;
    }

    static Color Res(string key, Color fallback) => Application.Current!.Resources.TryGetValue(key, out var v) && v is Color c ? c : fallback;
}
