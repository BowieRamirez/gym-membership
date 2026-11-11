using GymMembership.Core.Services;
using Supabase.Realtime;
using Supabase.Realtime.PostgresChanges;
using static Supabase.Realtime.PostgresChanges.PostgresChangesOptions;

namespace GymMembership.Core.Backend;

/// <summary>
/// F4 (Phase 7): Supabase Realtime on payments, check-ins and membership terms (0053), on the socket chat also uses. The server sends a
/// change only to someone whose row rules let them read the row. A hint only: pages ask the server again and keep their timers.
/// ponytail: like chat, the socket keeps the sign-in token; after about an hour the server may drop the channel and pages fall back to
/// their timers until the next sign-in. Refresh with SetAuth on token refresh if that bites.
/// </summary>
public sealed class SupabaseLiveRecords(SupabaseGateway gateway) : ILiveRecords
{
    readonly SemaphoreSlim gate = new(1, 1);
    RealtimeChannel? channel;
    bool signOutHooked;

    public event Action? Changed;

    public async Task StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (channel is not null || gateway.CurrentUserId is null) return;
            var client = await gateway.ClientAsync();
            if (!signOutHooked) { client.Auth.AddStateChangedListener((sender, state) => { if (state == Supabase.Gotrue.Constants.AuthState.SignedOut) _ = StopAsync(); }); signOutHooked = true; }
            var rt = await gateway.RealtimeAcquireAsync();
            try
            {
                var c = rt.Channel("live-records");
                foreach (var table in new[] { "payments", "gym_check_ins", "membership_terms" })
                    c.OnPostgresChange((_, _) => Changed?.Invoke(), ListenType.All, new PostgresChangesFilter { Table = table });
                await c.Subscribe();
                channel = c;
            }
            catch { await gateway.RealtimeReleaseAsync(); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (channel is not { } c) return;
            channel = null;
            try { c.Unsubscribe(); (await gateway.ClientAsync()).Realtime.Remove(c); } catch { /* the socket may already be gone */ }
            await gateway.RealtimeReleaseAsync();
        }
        finally { gate.Release(); }
    }
}
