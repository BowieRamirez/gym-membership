namespace GymMembership.Core.Services;

/// <summary>
/// Live updates for Messages. Every event is a hint: the app still asks the server for the truth, so a missed or doubled event
/// costs nothing. A failure of any kind only means Connected is false and the page keeps polling.
/// </summary>
public interface IRealtimeChat
{
    bool Connected { get; }
    event Action? ConnectionChanged;
    /// <summary>A message row was added or changed somewhere this user may read.</summary>
    event Action? Changed;
    /// <summary>The other side of the watched conversation is typing. The argument is that conversation's key (ChatThread.Key).</summary>
    event Action<string>? Typing;
    /// <summary>The ids of everyone online right now.</summary>
    event Action<IReadOnlySet<string>>? OnlineChanged;
    /// <summary>Connects and subscribes for this user. Safe to call again for the same user; a different user replaces the first.</summary>
    Task StartAsync(string userId);
    Task StopAsync();
    /// <summary>Joins the typing channel of one conversation, leaving the last one. Null just leaves.</summary>
    Task WatchAsync(string? conversationKey);
    Task SendTypingAsync(string conversationKey);
}

/// <summary>Demo mode and tests: never connects, never raises anything.</summary>
public sealed class NoRealtime : IRealtimeChat
{
    public static readonly NoRealtime Instance = new();
    public bool Connected => false;
#pragma warning disable CS0067   // the events exist for the interface and are never raised
    public event Action? ConnectionChanged;
    public event Action? Changed;
    public event Action<string>? Typing;
    public event Action<IReadOnlySet<string>>? OnlineChanged;
#pragma warning restore CS0067
    public Task StartAsync(string userId) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task WatchAsync(string? conversationKey) => Task.CompletedTask;
    public Task SendTypingAsync(string conversationKey) => Task.CompletedTask;
}

/// <summary>
/// F4 (Phase 7): a payment, check-in or membership term changed somewhere this user may read. A hint only, as with chat: the page that
/// hears it asks the server again, and keeps its own timer in case a hint never comes.
/// </summary>
public interface ILiveRecords
{
    event Action? Changed;
    /// <summary>Connects for the signed-in user. Safe to call again; a failure only means the page relies on its timer. Signing out stops it.</summary>
    Task StartAsync();
}

/// <summary>
/// A page's side of live updates: a hint starts a reload; hints that arrive while one runs cost one more reload, not one each.
/// A failed reload is the page's own business (it shows its error); the next hint still reloads.
/// </summary>
public sealed class LiveRefresh : IDisposable
{
    readonly ILiveRecords _live;
    readonly Func<Task> _reload;
    readonly object _sync = new();
    bool _running, _again, _gone;

    /// <summary>The reload in progress, or a finished task; for tests.</summary>
    public Task Idle { get; private set; } = Task.CompletedTask;

    public LiveRefresh(ILiveRecords live, Func<Task> reload)
    {
        (_live, _reload) = (live, reload);
        live.Changed += OnChanged;
    }

    void OnChanged()
    {
        lock (_sync)
        {
            if (_gone) return;
            if (_running) { _again = true; return; }
            _running = true;
            Idle = RunAsync();
        }
    }

    async Task RunAsync()
    {
        while (true)
        {
            try { await _reload(); } catch { /* the page shows its own error; the next hint tries again */ }
            lock (_sync)
            {
                if (!_again || _gone) { _running = false; return; }
                _again = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync) _gone = true;
        _live.Changed -= OnChanged;
    }
}

/// <summary>"Active 2 h ago", in words. Empty when the person has never been seen.</summary>
public static class ActiveAgo
{
    public static string Text(DateTime? seen, DateTime nowUtc)
    {
        if (seen is not { } s) return "";
        var utc = s.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : s.ToUniversalTime();   // the server sends UTC
        var ago = nowUtc - utc;
        if (ago < TimeSpan.FromMinutes(2)) return "Active just now";   // also covers a clock a little ahead
        if (ago < TimeSpan.FromHours(1)) return $"Active {(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromHours(24)) return $"Active {(int)ago.TotalHours} h ago";
        return $"Active {(int)ago.TotalDays} d ago";
    }
}
