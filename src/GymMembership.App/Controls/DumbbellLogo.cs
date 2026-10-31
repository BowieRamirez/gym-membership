namespace GymMembership.App.Controls;

/// <summary>
/// The app's logo: a lime dumbbell (two upright plates and a bar) on a black rounded square; Inverted swaps the two colours.
/// Drawn as vectors on a 64-unit grid, the same shapes as Resources/AppIcon/appiconfg.svg. Decorative: the app name sits beside it.
/// </summary>
public class DumbbellLogo : GraphicsView, IDrawable
{
    public static readonly BindableProperty SizeProperty = BindableProperty.Create(nameof(Size), typeof(double), typeof(DumbbellLogo), 40d,
        propertyChanged: (b, _, n) => { var logo = (DumbbellLogo)b; logo.WidthRequest = logo.HeightRequest = (double)n; });
    public static readonly BindableProperty InvertedProperty = BindableProperty.Create(nameof(Inverted), typeof(bool), typeof(DumbbellLogo), false,
        propertyChanged: (b, _, _) => ((DumbbellLogo)b).Invalidate());

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool Inverted { get => (bool)GetValue(InvertedProperty); set => SetValue(InvertedProperty, value); }

    /// <summary>Just the lime dumbbell with a soft lime glow, no square: the big mark on the sign-in panel.</summary>
    public static readonly BindableProperty BareProperty = BindableProperty.Create(nameof(Bare), typeof(bool), typeof(DumbbellLogo), false,
        propertyChanged: (b, _, _) => ((DumbbellLogo)b).Invalidate());
    public bool Bare { get => (bool)GetValue(BareProperty); set => SetValue(BareProperty, value); }

    public DumbbellLogo()
    {
        Drawable = this;
        WidthRequest = HeightRequest = Size;
        BackgroundColor = Colors.Transparent;
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
    }

    public void Draw(ICanvas canvas, RectF rect)
    {
        var lime = Color.FromArgb("#E4F58A");
        var unit = Math.Min(rect.Width, rect.Height) / 64f;
        if (Bare) { canvas.SetShadow(SizeF.Zero, 16 * unit, lime.WithAlpha(0.35f)); canvas.FillColor = lime; }
        else
        {
            canvas.FillColor = Inverted ? lime : Colors.Black;
            canvas.FillRoundedRectangle(0, 0, 64 * unit, 64 * unit, 18 * unit);
            canvas.FillColor = Inverted ? Colors.Black : lime;
        }
        canvas.FillRoundedRectangle(12 * unit, 22 * unit, 8 * unit, 20 * unit, 3 * unit);
        canvas.FillRoundedRectangle(44 * unit, 22 * unit, 8 * unit, 20 * unit, 3 * unit);
        canvas.FillRoundedRectangle(20 * unit, 29 * unit, 24 * unit, 6 * unit, 2 * unit);
    }
}
