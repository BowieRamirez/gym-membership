using System.Windows.Input;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

/// <summary>Loads the page's ViewModel each time the page appears.</summary>
public class BasePage : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (Updated is { } bar && !_barHooked) { bar.Refreshing += RefreshNowAsync; _barHooked = true; }
        if (LiveRecords && _live is null && IPlatformApplication.Current?.Services.GetService<Core.Services.ILiveRecords>() is { } live)
        {
            _live = new Core.Services.LiveRefresh(live, () => Dispatcher.DispatchAsync(RefreshNowAsync));
            try { await live.StartAsync(); } catch { /* no live updates: the timer and Refresh still work */ }
        }
        await Task.WhenAll(new[] { BindingContext, _part?.BindingContext }.OfType<LoadableViewModel>().Select(vm => vm.OnAppearingAsync()));
        MarkUpdated();
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
        _live?.Dispose(); _live = null;   // the shared connection stays up; only this page stops listening
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

    /// <summary>
    /// F17: another page's content shown below this one's (no switch), loaded and released with this page. Its scroll view is dropped and
    /// its padding zeroed, so the combined page scrolls as one.
    /// </summary>
    protected void HostBelow(ContentView host, ContentPage part)
    {
        _part = part;
        var content = part.Content;
        if (content is ScrollView { Content: { } inner } scroll) { scroll.Content = null; content = inner; } else part.Content = null;
        if (content is Layout layout) layout.Padding = 0;
        content.BindingContext = part.BindingContext;
        host.Content = content;
    }

    /// <summary>A page that shows live data returns how often to reload while it is open; null means only when it opens.</summary>
    protected virtual TimeSpan? AutoRefresh => null;
    protected virtual bool RefreshOwnViewModel => true;

    /// <summary>F4: a page that shows payments, check-ins or membership terms reloads when one changes (and keeps its timer).</summary>
    protected virtual bool LiveRecords => false;
    /// <summary>F4: the page's "Updated 6:40 PM" line with Refresh, if it has one.</summary>
    protected virtual Controls.UpdatedBar? Updated => null;

    IDispatcherTimer? _refresh;
    Core.Services.LiveRefresh? _live;
    bool _barHooked;

    async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (AutoRefresh is { } every && _refresh is not null) _refresh.Interval = every;   // a page whose pace depends on its connection
        await RefreshNowAsync();
    }

    // the timer, a live hint and Refresh all reload the same quiet way
    async Task RefreshNowAsync()
    {
        if (Updated is { } bar) bar.Busy = true;
        try
        {
            var part = _part is BasePage { AutoRefresh: not null } ? _part.BindingContext : null;   // a part refreshes only if it did on its own
            await Task.WhenAll(new[] { RefreshOwnViewModel ? BindingContext : null, part }.OfType<LoadableViewModel>().Select(vm => vm.AutoRefreshAsync()));
            MarkUpdated();
        }
        finally { if (Updated is { } b) b.Busy = false; }
    }

    void MarkUpdated() { if (Updated is { } bar && (BindingContext as LoadableViewModel)?.ErrorMessage is null) bar.Mark(DateTime.Now); }

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
