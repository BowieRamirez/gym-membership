namespace GymMembership.App.Controls;

/// <summary>
/// The HF monogram (Phase 9, D24): a yellow ring and yellow block letters H and F on a black disc, after the "Health &amp; Fitness Gym
/// Center" badge. Drawn as plain shapes on a 64-unit grid, so it needs no font and stays sharp at any size; the same shapes as
/// Resources/AppIcon/appiconfg.svg and Resources/Splash/splash.svg. Decorative: the app name sits beside it or in its tooltip.
/// </summary>
public class LogoMark : GraphicsView, IDrawable
{
    public static readonly BindableProperty SizeProperty = BindableProperty.Create(nameof(Size), typeof(double), typeof(LogoMark), 40d,
        propertyChanged: (b, _, n) => { var mark = (LogoMark)b; mark.WidthRequest = mark.HeightRequest = (double)n; });
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public LogoMark()
    {
        Drawable = this;
        WidthRequest = HeightRequest = Size;
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
    }

    /// <summary>The letters as (x, y, width, height) on the 64-unit grid: H then F, 4.5 units thick, 20 tall, centred.</summary>
    internal static readonly (float X, float Y, float W, float H)[] Letters =
    [
        (17.5f, 22, 4.5f, 20), (26, 22, 4.5f, 20), (17.5f, 29.75f, 13, 4.5f),   // H
        (34, 22, 4.5f, 20), (34, 22, 12.5f, 4.5f), (34, 29.75f, 10, 4.5f),      // F
    ];

    public void Draw(ICanvas canvas, RectF rect)
    {
        var u = Math.Min(rect.Width, rect.Height) / 64f;
        var yellow = Color.FromArgb("#FFEC00");
        canvas.FillColor = Color.FromArgb("#111111");
        canvas.FillCircle(32 * u, 32 * u, 32 * u);
        canvas.StrokeColor = yellow;
        canvas.StrokeSize = Math.Max(1.5f, 3.2f * u);   // a hairline ring vanishes at title-bar size
        canvas.DrawCircle(32 * u, 32 * u, 24.8f * u);
        canvas.FillColor = yellow;
        foreach (var (x, y, w, h) in Letters) canvas.FillRoundedRectangle(x * u, y * u, w * u, h * u, 0.8f * u);
    }
}
