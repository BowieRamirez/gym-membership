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
        ["bell"] = "M 6 8 C 6 4.686 8.686 2 12 2 C 15.314 2 18 4.686 18 8 C 18 15 21 17 21 17 L 3 17 C 3 17 6 15 6 8 Z M 10.3 21 C 10.646 21.598 11.29 22 12 22 C 12.71 22 13.354 21.598 13.7 21",
        // side-menu page icons (Lucide shapes, circles and corners redrawn as curves)
        ["layout-dashboard"] = "M 4 3 L 9 3 C 9.552 3 10 3.448 10 4 L 10 11 C 10 11.552 9.552 12 9 12 L 4 12 C 3.448 12 3 11.552 3 11 L 3 4 C 3 3.448 3.448 3 4 3 Z M 15 3 L 20 3 C 20.552 3 21 3.448 21 4 L 21 7 C 21 7.552 20.552 8 20 8 L 15 8 C 14.448 8 14 7.552 14 7 L 14 4 C 14 3.448 14.448 3 15 3 Z M 15 12 L 20 12 C 20.552 12 21 12.448 21 13 L 21 20 C 21 20.552 20.552 21 20 21 L 15 21 C 14.448 21 14 20.552 14 20 L 14 13 C 14 12.448 14.448 12 15 12 Z M 4 16 L 9 16 C 9.552 16 10 16.448 10 17 L 10 20 C 10 20.552 9.552 21 9 21 L 4 21 C 3.448 21 3 20.552 3 20 L 3 17 C 3 16.448 3.448 16 4 16 Z",
        ["house"] = "M 15 21 L 15 13 C 15 12.448 14.552 12 14 12 L 10 12 C 9.448 12 9 12.448 9 13 L 9 21 M 3 10 C 3 9.4 3.27 8.84 3.71 8.46 L 10.71 2.46 C 11.45 1.84 12.55 1.84 13.29 2.46 L 20.29 8.46 C 20.73 8.84 21 9.4 21 10 L 21 19 C 21 20.105 20.105 21 19 21 L 5 21 C 3.895 21 3 20.105 3 19 Z",
        ["badge-check"] = "M 22 12 C 22 17.523 17.523 22 12 22 C 6.477 22 2 17.523 2 12 C 2 6.477 6.477 2 12 2 C 17.523 2 22 6.477 22 12 Z M 9 12 L 11 14 L 15 10",
        ["scan-line"] = "M 3 7 L 3 5 C 3 3.895 3.895 3 5 3 L 7 3 M 17 3 L 19 3 C 20.105 3 21 3.895 21 5 L 21 7 M 21 17 L 21 19 C 21 20.105 20.105 21 19 21 L 17 21 M 7 21 L 5 21 C 3.895 21 3 20.105 3 19 L 3 17 M 7 12 L 17 12",
        ["users"] = "M 13 7 C 13 9.209 11.209 11 9 11 C 6.791 11 5 9.209 5 7 C 5 4.791 6.791 3 9 3 C 11.209 3 13 4.791 13 7 Z M 16 21 L 16 19 C 16 16.791 14.209 15 12 15 L 6 15 C 3.791 15 2 16.791 2 19 L 2 21 M 16 3.13 C 17.77 3.583 19.008 5.173 19.008 7 C 19.008 8.827 17.77 10.417 16 10.87 M 22 21 L 22 19 C 21.999 17.177 20.765 15.586 19 15.13",
        ["package"] = "M 11 21.73 C 11.619 22.087 12.381 22.087 13 21.73 L 20 17.73 C 20.618 17.373 20.999 16.714 21 16 L 21 8 C 20.999 7.286 20.618 6.627 20 6.27 L 13 2.27 C 12.381 1.913 11.619 1.913 11 2.27 L 4 6.27 C 3.382 6.627 3.001 7.286 3 8 L 3 16 C 3.001 16.714 3.382 17.373 4 17.73 Z M 12 22 L 12 12 M 3.29 7 L 12 12 L 20.71 7 M 7.5 4.27 L 16.5 9.42",
        ["tag"] = "M 12.586 2.586 C 12.211 2.211 11.702 2 11.172 2 L 4 2 C 2.895 2 2 2.895 2 4 L 2 11.172 C 2 11.702 2.211 12.211 2.586 12.586 L 11.29 21.29 C 12.204 22.198 13.68 22.198 14.594 21.29 L 21.29 14.594 C 22.198 13.68 22.198 12.204 21.29 11.29 Z M 8.5 7.5 C 8.5 8.052 8.052 8.5 7.5 8.5 C 6.948 8.5 6.5 8.052 6.5 7.5 C 6.5 6.948 6.948 6.5 7.5 6.5 C 8.052 6.5 8.5 6.948 8.5 7.5 Z",
        ["undo"] = "M 9 14 L 4 9 L 9 4 M 4 9 L 14.5 9 C 17.538 9 20 11.462 20 14.5 C 20 17.538 17.538 20 14.5 20 L 11 20",
        ["ban"] = "M 22 12 C 22 17.523 17.523 22 12 22 C 6.477 22 2 17.523 2 12 C 2 6.477 6.477 2 12 2 C 17.523 2 22 6.477 22 12 Z M 4.93 4.93 L 19.07 19.07",
        ["sliders-horizontal"] = "M 21 4 L 14 4 M 10 4 L 3 4 M 21 12 L 12 12 M 8 12 L 3 12 M 21 20 L 16 20 M 12 20 L 3 20 M 14 2 L 14 6 M 8 10 L 8 14 M 16 18 L 16 22",
        ["chart-line"] = "M 3 3 L 3 19 C 3 20.105 3.895 21 5 21 L 21 21 M 7 16 L 11 12 L 15 15 L 20 9",
        ["archive"] = "M 3 3 L 21 3 C 21.552 3 22 3.448 22 4 L 22 7 C 22 7.552 21.552 8 21 8 L 3 8 C 2.448 8 2 7.552 2 7 L 2 4 C 2 3.448 2.448 3 3 3 Z M 4 8 L 4 19 C 4 20.105 4.895 21 6 21 L 18 21 C 19.105 21 20 20.105 20 19 L 20 8 M 10 12 L 14 12",
        ["settings"] = "M 12 2 L 15.827 2.761 L 17.374 6.626 L 19.021 9.092 L 22 12 L 21.239 15.827 L 17.374 17.374 L 14.908 19.021 L 12 22 L 8.173 21.239 L 6.626 17.374 L 4.979 14.908 L 2 12 L 2.761 8.173 L 6.626 6.626 L 9.092 4.979 Z M 15 12 C 15 13.657 13.657 15 12 15 C 10.343 15 9 13.657 9 12 C 9 10.343 10.343 9 12 9 C 13.657 9 15 10.343 15 12 Z",
        ["id-card"] = "M 4 5 L 20 5 C 21.105 5 22 5.895 22 7 L 22 17 C 22 18.105 21.105 19 20 19 L 4 19 C 2.895 19 2 18.105 2 17 L 2 7 C 2 5.895 2.895 5 4 5 Z M 11 11 C 11 12.105 10.105 13 9 13 C 7.895 13 7 12.105 7 11 C 7 9.895 7.895 9 9 9 C 10.105 9 11 9.895 11 11 Z M 6 16 C 6.6 14.8 7.7 14 9 14 C 10.3 14 11.4 14.8 12 16 M 15 10 L 18 10 M 15 14 L 18 14",
        ["calendar-plus"] = "M 5 4 L 19 4 C 20.105 4 21 4.895 21 6 L 21 20 C 21 21.105 20.105 22 19 22 L 5 22 C 3.895 22 3 21.105 3 20 L 3 6 C 3 4.895 3.895 4 5 4 Z M 16 2 L 16 6 M 8 2 L 8 6 M 3 10 L 21 10 M 12 14 L 12 18 M 10 16 L 14 16",
        ["dumbbell"] = "M 3 9 L 4 9 C 4.552 9 5 9.448 5 10 L 5 14 C 5 14.552 4.552 15 4 15 L 3 15 C 2.448 15 2 14.552 2 14 L 2 10 C 2 9.448 2.448 9 3 9 Z M 6 6 L 7 6 C 7.552 6 8 6.448 8 7 L 8 17 C 8 17.552 7.552 18 7 18 L 6 18 C 5.448 18 5 17.552 5 17 L 5 7 C 5 6.448 5.448 6 6 6 Z M 17 6 L 18 6 C 18.552 6 19 6.448 19 7 L 19 17 C 19 17.552 18.552 18 18 18 L 17 18 C 16.448 18 16 17.552 16 17 L 16 7 C 16 6.448 16.448 6 17 6 Z M 20 9 L 21 9 C 21.552 9 22 9.448 22 10 L 22 14 C 22 14.552 21.552 15 21 15 L 20 15 C 19.448 15 19 14.552 19 14 L 19 10 C 19 9.448 19.448 9 20 9 Z M 8 12 L 16 12",
        ["calendar-days"] = "M 5 4 L 19 4 C 20.105 4 21 4.895 21 6 L 21 20 C 21 21.105 20.105 22 19 22 L 5 22 C 3.895 22 3 21.105 3 20 L 3 6 C 3 4.895 3.895 4 5 4 Z M 16 2 L 16 6 M 8 2 L 8 6 M 3 10 L 21 10 M 8 14 L 8.01 14 M 12 14 L 12.01 14 M 16 14 L 16.01 14 M 8 18 L 8.01 18 M 12 18 L 12.01 18 M 16 18 L 16.01 18",
        ["clock"] = "M 22 12 C 22 17.523 17.523 22 12 22 C 6.477 22 2 17.523 2 12 C 2 6.477 6.477 2 12 2 C 17.523 2 22 6.477 22 12 Z M 12 6 L 12 12 L 16 14",
        ["calendar-clock"] = "M 21 7.5 L 21 6 C 21 4.895 20.105 4 19 4 L 5 4 C 3.895 4 3 4.895 3 6 L 3 20 C 3 21.105 3.895 22 5 22 L 8.5 22 M 16 2 L 16 6 M 8 2 L 8 6 M 3 10 L 8 10 M 22 16 C 22 19.314 19.314 22 16 22 C 12.686 22 10 19.314 10 16 C 10 12.686 12.686 10 16 10 C 19.314 10 22 12.686 22 16 Z M 16 14 L 16 16 L 17.6 17",
        ["user-round"] = "M 17 8 C 17 10.761 14.761 13 12 13 C 9.239 13 7 10.761 7 8 C 7 5.239 9.239 3 12 3 C 14.761 3 17 5.239 17 8 Z M 20 21 C 20 16.582 16.418 13 12 13 C 7.582 13 4 16.582 4 21",
        ["briefcase"] = "M 4 7 L 20 7 C 21.105 7 22 7.895 22 9 L 22 19 C 22 20.105 21.105 21 20 21 L 4 21 C 2.895 21 2 20.105 2 19 L 2 9 C 2 7.895 2.895 7 4 7 Z M 16 21 L 16 5 C 16 3.895 15.105 3 14 3 L 10 3 C 8.895 3 8 3.895 8 5 L 8 21",
        ["log-out"] = "M 9 21 L 5 21 C 3.895 21 3 20.105 3 19 L 3 5 C 3 3.895 3.895 3 5 3 L 9 3 M 16 17 L 21 12 L 16 7 M 21 12 L 9 12",
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
/// One of the Icons: drawn black in both themes on a light rounded chip (lime when Selected), so it reads on any surface.
/// Size is the drawing, ChipSize the chip around it. It is decorative (the words beside it say the same) and lets touches through.
/// </summary>
public class IconView : ContentView
{
    public static readonly BindableProperty IconProperty = BindableProperty.Create(nameof(Icon), typeof(string), typeof(IconView), "",
        propertyChanged: (b, _, _) => ((IconView)b).Redraw());
    public static readonly BindableProperty SelectedProperty = BindableProperty.Create(nameof(Selected), typeof(bool), typeof(IconView), false,
        propertyChanged: (b, _, _) => ((IconView)b).Recolour());
    public static readonly BindableProperty SizeProperty = BindableProperty.Create(nameof(Size), typeof(double), typeof(IconView), 18.0,
        propertyChanged: (b, _, _) => ((IconView)b).Resize());
    public static readonly BindableProperty ChipSizeProperty = BindableProperty.Create(nameof(ChipSize), typeof(double), typeof(IconView), 36.0,
        propertyChanged: (b, _, _) => ((IconView)b).Resize());

    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public bool Selected { get => (bool)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double ChipSize { get => (double)GetValue(ChipSizeProperty); set => SetValue(ChipSizeProperty, value); }

    /// <summary>No chip, a muted line that follows the theme: for an icon inside a field, like the password eye.</summary>
    public static readonly BindableProperty GhostProperty = BindableProperty.Create(nameof(Ghost), typeof(bool), typeof(IconView), false,
        propertyChanged: (b, _, _) => ((IconView)b).Recolour());
    public bool Ghost { get => (bool)GetValue(GhostProperty); set => SetValue(GhostProperty, value); }

    readonly Microsoft.Maui.Controls.Shapes.Path _path = new()
    {
        Aspect = Stretch.Uniform, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center
    };
    readonly Border _chip = new() { StrokeThickness = 0, Padding = 0 };

    public IconView()
    {
        InputTransparent = true;
        AutomationProperties.SetIsInAccessibleTree(this, false);
        _path.Stroke = new SolidColorBrush(Pick("IconInk", Colors.Black));
        _chip.Content = _path;
        Content = _chip;
        Resize();
        Recolour();
    }

    static Color Pick(string key, Color fallback) => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c ? c : fallback;

    void Redraw() =>
        _path.Data = Icons.Data.TryGetValue(Icon ?? "", out var d) ? new PathGeometryConverter().ConvertFromInvariantString(d) as Geometry : null;

    void Resize()
    {
        WidthRequest = HeightRequest = ChipSize;
        _path.WidthRequest = _path.HeightRequest = Size;
        _path.StrokeThickness = Size < 14 ? 2.5 : 2;   // tiny marks need a heavier line to stay visible
        _chip.StrokeShape = new RoundRectangle { CornerRadius = ChipSize / 3 };
    }

    void Recolour()
    {
        if (Ghost)
        {
            _chip.BackgroundColor = Colors.Transparent;
            _path.SetAppTheme<Brush>(Shape.StrokeProperty, new SolidColorBrush(Pick("MutedLight", Colors.Gray)), new SolidColorBrush(Pick("MutedDark", Colors.Gray)));
            return;
        }
        var lime = Pick("Lime", Colors.YellowGreen);
        _chip.SetAppThemeColor(BackgroundColorProperty,
            Selected ? lime : Pick("IconChipLight", Colors.White), Selected ? lime : Pick("IconChipDark", Colors.LightGray));
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

    readonly IconView _icon = new() { Size = 20, ChipSize = 32, Ghost = true, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
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
/// A choice button with an icon on its left. It stays a real Button (touch, keyboard, screen reader); IsSelected fills it lime (the icon
/// stays black) and is announced as "selected".
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

    readonly Button _button = new() { Padding = new Thickness(44, 0, 16, 0) };   // room on the left for the icon
    readonly IconView _icon = new() { Size = 16, ChipSize = 28, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(8, 0, 0, 0) };

    public IconButton()
    {
        Content = new Grid { Children = { _button, _icon } };
        Refresh();
    }

    void Refresh()
    {
        _button.Text = Text; _button.Command = Command; _button.CommandParameter = CommandParameter;
        _icon.Icon = Icon;
        if (Application.Current!.Resources.TryGetValue(IsSelected ? "LimeSmallButton" : "SmallOutlineButton", out var style)) _button.Style = (Style)style;
        SemanticProperties.SetDescription(_button, IsSelected ? $"{Text}, selected" : Text);
    }
}
