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
