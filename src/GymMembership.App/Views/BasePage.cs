using System.Windows.Input;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

/// <summary>Loads the page's ViewModel each time the page appears.</summary>
public class BasePage : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Task.WhenAll(new[] { BindingContext, _part?.BindingContext }.OfType<LoadableViewModel>().Select(vm => vm.OnAppearingAsync()));
        if (AutoRefresh is { } every)
        {
            _refresh ??= Dispatcher.CreateTimer();
            _refresh.Interval = every;
            _refresh.Tick -= OnRefreshTick;
            _refresh.Tick += OnRefreshTick;
            _refresh.Start();
        }
    }

    protected override void OnDisappearing()
    {
        _refresh?.Stop();
        (BindingContext as LoadableViewModel)?.OnDisappearing();
        (_part?.BindingContext as LoadableViewModel)?.OnDisappearing();
        base.OnDisappearing();
    }

    ContentPage? _part;

    /// <summary>
    /// A combined page (Controls.PartSwitch): another page's content shown in <paramref name="host"/>, loaded, refreshed and released
    /// with this one. That page keeps its binding context and its own button handlers; only its content moves here.
    /// </summary>
    protected void HostPart(Controls.PartSwitch partSwitch, ContentView host, ContentPage part)
    {
        // a phone header fits about 15 characters beside the back arrow and picture: it names the open half instead of both
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && _part is null)
        {
            Title = partSwitch.First;
            partSwitch.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Controls.PartSwitch.ShowSecond)) Title = partSwitch.ShowSecond ? partSwitch.Second : partSwitch.First;
            };
        }
        _part = part;
        var content = part.Content;
        part.Content = null;
        content.BindingContext = part.BindingContext;
        host.Content = content;
    }

    /// <summary>A page that shows live data returns how often to reload while it is open; null means only when it opens.</summary>
    protected virtual TimeSpan? AutoRefresh => null;

    IDispatcherTimer? _refresh;

    async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (AutoRefresh is { } every && _refresh is not null) _refresh.Interval = every;   // a page whose pace depends on its connection
        var part = _part is BasePage { AutoRefresh: not null } ? _part.BindingContext : null;   // a part refreshes only if it did on its own
        await Task.WhenAll(new[] { BindingContext, part }.OfType<LoadableViewModel>().Select(vm => vm.AutoRefreshAsync()));
    }

    /// <summary>
    /// Runs a view-model command for the list row a button (or tap) belongs to. Used instead of binding the command through
    /// RelativeSource AncestorType: while a list reloads, MAUI re-evaluates that compiled binding with no ancestor left, the generated
    /// code dereferences null, and the debugger stops on a NullReferenceException. A plain Clicked handler has nothing to look up.
    /// </summary>
    protected void Row<TVm>(object? sender, Func<TVm, ICommand> command, Func<object, object?>? parameter = null) where TVm : class
    {
        if ((sender as BindableObject)?.BindingContext is not { } row || BindingContext is not TVm vm) return;
        var cmd = command(vm);
        var arg = parameter is null ? row : parameter(row);
        if (cmd.CanExecute(arg)) cmd.Execute(arg);
    }
}
