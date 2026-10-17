#if WINDOWS
using NavigationView = Microsoft.UI.Xaml.Controls.NavigationView;

namespace GymMembership.App.Controls;

public partial class HeaderBar
{
    const string SidebarAnimation = "WindowsSidebar";
    bool _sidebarCollapsed;
    double _sidebarWidth;

    void SetupWindowsSidebar(Shell shell)
    {
        var toggle = new Button
        {
            Text = "\uE700", FontFamily = "Segoe MDL2 Assets", FontSize = 18,
            WidthRequest = 48, HeightRequest = 48, Padding = 0,
            BackgroundColor = Colors.Transparent, BorderWidth = 0,
            VerticalOptions = LayoutOptions.Center, AutomationId = "WindowsSidebarToggle"
        };
        toggle.SetAppThemeColor(Button.TextColorProperty,
            (Color)Application.Current!.Resources["InkLight"], (Color)Application.Current.Resources["InkDark"]);
        void UpdateLabel()
        {
            var label = _sidebarCollapsed ? "Expand navigation" : "Collapse navigation";
            SemanticProperties.SetDescription(toggle, label);
            ToolTipProperties.SetText(toggle, label);
        }
        UpdateLabel();

        var grid = (Grid)Content;
        foreach (var child in grid.Children) grid.SetColumn(child, grid.GetColumn(child) + 1);
        grid.ColumnDefinitions.Insert(0, new ColumnDefinition(GridLength.Auto));
        grid.Children.Insert(0, toggle);

        NavigationView? Navigation() => shell.Handler?.PlatformView as NavigationView;
        void Finish(NavigationView navigation)
        {
            navigation.OpenPaneLength = _sidebarCollapsed ? 0 : _sidebarWidth;
            navigation.IsPaneVisible = !_sidebarCollapsed;
        }
        void Restore()
        {
            if (Navigation() is not { } navigation) return;
            if (_sidebarWidth == 0) _sidebarWidth = navigation.OpenPaneLength;
            navigation.CompactPaneLength = 0;
            Finish(navigation);
        }
        shell.Loaded += (_, _) => Restore();
        shell.Unloaded += (_, _) =>
        {
            shell.AbortAnimation(SidebarAnimation);
            if (Navigation() is { } navigation) Finish(navigation);
        };

        toggle.Clicked += (_, _) =>
        {
            if (Navigation() is not { } navigation) return;
            if (_sidebarWidth == 0) _sidebarWidth = navigation.OpenPaneLength;
            shell.AbortAnimation(SidebarAnimation);
            _sidebarCollapsed = !_sidebarCollapsed;
            UpdateLabel();
            navigation.IsPaneVisible = true;
            navigation.CompactPaneLength = 0;
            var target = _sidebarCollapsed ? 0 : _sidebarWidth;
            if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                Finish(navigation);
                return;
            }

            // Keep the native pane content at its full width: slide/clip it rather than squeezing its labels.
            new Animation(width => navigation.OpenPaneLength = width, navigation.OpenPaneLength, target)
                .Commit(shell, SidebarAnimation, length: 220, easing: Easing.CubicInOut,
                    finished: (_, cancelled) => { if (!cancelled) Finish(navigation); });
        };
    }
}
#endif
