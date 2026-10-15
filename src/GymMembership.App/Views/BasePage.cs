using System.Windows.Input;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

/// <summary>Loads the page's ViewModel each time the page appears.</summary>
public class BasePage : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is LoadableViewModel vm) await vm.OnAppearingAsync();
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
        base.OnDisappearing();
    }

    /// <summary>A page that shows live data returns how often to reload while it is open; null means only when it opens.</summary>
    protected virtual TimeSpan? AutoRefresh => null;

    IDispatcherTimer? _refresh;

    async void OnRefreshTick(object? sender, EventArgs e)
    { if (BindingContext is LoadableViewModel live) await live.OnAppearingAsync(); }

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
