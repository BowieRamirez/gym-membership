namespace GymMembership.App.Controls;

/// <summary>
/// F4 (Phase 7): "Updated 6:40 PM", a spinner while it loads, and Refresh, as on the System admin Overview. The page (BasePage) marks it
/// after each load it makes, opened, timed or live, and runs its reload when Refresh is pressed.
/// </summary>
public sealed class UpdatedBar : Grid
{
    readonly Label _text = new() { VerticalOptions = LayoutOptions.Center, AutomationId = "UpdatedText" };
    readonly ActivityIndicator _busy = new() { WidthRequest = 20, HeightRequest = 20, VerticalOptions = LayoutOptions.Center, IsVisible = false };

    /// <summary>Refresh was pressed; BasePage reloads.</summary>
    public event Func<Task>? Refreshing;

    public UpdatedBar()
    {
        ColumnSpacing = 12;
        ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)];
        _text.Style = (Style)Application.Current!.Resources["Muted"];
        var button = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0, WidthRequest = 40, HeightRequest = 40, CornerRadius = 12, AutomationId = "UpdatedRefresh" };
        SemanticProperties.SetDescription(button, "Refresh");
        ToolTipProperties.SetText(button, "Refresh");
        button.Clicked += async (_, _) => { if (Refreshing is { } run) await run(); };
        var icon = new IconView { Icon = "refresh-cw", Size = 16, ChipSize = 36, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
        Add(_text);
        this.Add(_busy, 1);
        this.Add(new Grid { WidthRequest = 40, HeightRequest = 40, VerticalOptions = LayoutOptions.Center, Children = { button, icon } }, 2);
    }

    public bool Busy { set => _busy.IsVisible = _busy.IsRunning = value; }

    public void Mark(DateTime local) => _text.Text = $"Updated {local:h:mm tt}";
}
