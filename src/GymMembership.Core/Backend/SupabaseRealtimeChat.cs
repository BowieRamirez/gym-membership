using GymMembership.Core.Services;
using Supabase.Realtime;
using Supabase.Realtime.Broadcast;
using Supabase.Realtime.Interfaces;
using Supabase.Realtime.Models;
using Supabase.Realtime.Presence;
using Supabase.Realtime.PostgresChanges;
using static Supabase.Realtime.PostgresChanges.PostgresChangesOptions;

namespace GymMembership.Core.Backend;

/// <summary>
/// Supabase Realtime for Messages. Row changes are only a hint (the app asks the server again); typing is a broadcast with no content;
/// online is presence on one channel, "online".
/// ponytail: the online channel is public, so anyone holding the app's public key can list the ids (UUIDs, no names) of everyone online.
/// A private channel with a policy on realtime.messages is the upgrade if that ever matters.
/// ponytail: the token Realtime holds is the one from sign-in; after about an hour the server may drop the row-change channel while
/// Connected still says true, and the page then relies on its 30 second timer until the next page visit. Refresh with SetAuth if that bites.
/// </summary>
public sealed class SupabaseRealtimeChat(SupabaseGateway gateway) : IRealtimeChat
{
    // nobody reads these fields: a ping is only "someone typed", and presence is only "this id is here"
    sealed class TypingPayload { public string From { get; set; } = ""; }
    sealed class TypingBroadcast : BaseBroadcast<TypingPayload> { }
    sealed class Here : BasePresence { public string At { get; set; } = ""; }

    readonly SemaphoreSlim gate = new(1, 1);
    RealtimeChannel? changes, online, typing;
    RealtimeBroadcast<TypingBroadcast>? typingBroadcast;
    RealtimePresence<Here>? presence;
    string? userId, watching;
    IRealtimeClient<RealtimeSocket, RealtimeChannel>.SocketStateEventHandler? stateHandler;   // added on each start and removed on each stop, so nothing depends on what Disconnect() clears
    bool signOutHooked;

    public bool Connected { get; private set; }
    public event Action? ConnectionChanged;
    public event Action? Changed;
    public event Action<string>? Typing;
    public event Action<IReadOnlySet<string>>? OnlineChanged;

    void SetConnected(bool on) { if (Connected == on) return; Connected = on; ConnectionChanged?.Invoke(); }

    public async Task StartAsync(string user)
    {
        await gate.WaitAsync();
        try
        {
            if (userId == user && Connected) return;
            await StopCoreAsync();
            var client = await gateway.ClientAsync();
            // signing out ends the connection and the "online" presence at once, whether or not the page is told it went away
            if (!signOutHooked) { client.Auth.AddStateChangedListener((sender, state) => { if (state == Supabase.Gotrue.Constants.AuthState.SignedOut) _ = StopAsync(); }); signOutHooked = true; }
            var rt = client.Realtime;
            if (gateway.AccessToken is { } token) rt.SetAuth(token);   // so the server filters changes by this user's row access
            await rt.ConnectAsync();
            stateHandler = (_, state) => SetConnected(state == Constants.SocketState.Open && userId is not null);
            rt.AddStateChangedHandler(stateHandler);

            changes = rt.Channel("messenger-changes");
            changes.OnPostgresChange((_, _) => Changed?.Invoke(), ListenType.All, new PostgresChangesFilter { Table = "messages" });
            changes.OnPostgresChange((_, _) => Changed?.Invoke(), ListenType.All, new PostgresChangesFilter { Table = "support_messages" });
            await changes.Subscribe();

            online = rt.Channel("online");
            var here = presence = online.Register<Here>(user);
            here.AddPresenceEventHandler(IRealtimePresence.EventType.Sync, (_, _) => OnlineChanged?.Invoke(here.CurrentState.Keys.ToHashSet()));
            await online.Subscribe();
            await here.Track(new Here { At = DateTime.UtcNow.ToString("o") });

            userId = user;
            SetConnected(true);
        }
        catch { await StopCoreAsync(); throw; }   // the view model treats a throw as "not connected"
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try { await StopCoreAsync(); } finally { gate.Release(); }
    }

    async Task StopCoreAsync()
    {
        SetConnected(false);
        userId = null; watching = null;
        try { presence?.Untrack(); } catch { /* the socket may already be gone */ }
        foreach (var c in new[] { changes, online, typing })
            try { if (c is not null) { c.Unsubscribe(); (await gateway.ClientAsync()).Realtime.Remove(c); } } catch { /* same */ }
        changes = online = typing = null; presence = null; typingBroadcast = null;
        try
        {
            var rt = (await gateway.ClientAsync()).Realtime;
            if (stateHandler is not null) rt.RemoveStateChangedHandler(stateHandler);
            rt.Disconnect();
        }
        catch { /* same */ }
        stateHandler = null;
    }

    public async Task WatchAsync(string? key)
    {
        await gate.WaitAsync();
        try
        {
            if (key == watching || userId is null) return;
            if (typing is not null)
            {
                try { typing.Unsubscribe(); (await gateway.ClientAsync()).Realtime.Remove(typing); } catch { /* same */ }
                typing = null; typingBroadcast = null;
            }
            watching = key;
            if (key is null) return;
            var rt = (await gateway.ClientAsync()).Realtime;
            typing = rt.Channel("typing:" + key.Replace('|', ':'));
            typingBroadcast = typing.Register<TypingBroadcast>(broadcastSelf: false);
            typingBroadcast.AddBroadcastEventHandler((_, _) => Typing?.Invoke(key));
            await typing.Subscribe();
        }
        finally { gate.Release(); }
    }

    public async Task SendTypingAsync(string key)
    {
        if (key != watching || typingBroadcast is not { } b || userId is null) return;
        await b.Send("typing", new TypingBroadcast { Payload = new TypingPayload { From = userId } });
    }
}
