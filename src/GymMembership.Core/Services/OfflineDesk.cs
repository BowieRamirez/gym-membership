using System.Text.Json;

namespace GymMembership.Core.Services;

/// <summary>Where the desk keeps its saved list and queue between runs.</summary>
public interface IDeskStore
{
    string? Load();
    void Save(string text);
}

public sealed class SavedTerm { public DateTime Starts { get; set; } public DateTime Ends { get; set; } }
public sealed class SavedMember
{
    public string Code { get; set; } = "";
    public string Member { get; set; } = "";
    public string Name { get; set; } = "";
    public List<SavedTerm> Terms { get; set; } = new();
}
public sealed record QueuedCheckIn(Guid Ref, string MemberId, string Name, DateTime AtUtc);
public sealed record RefusedCheckIn(string Name, DateTime AtUtc, string Why);

/// <summary>
/// D12: keeps the desk working when the connection drops. It shows a saved list, never a live status, and every check-in it queues is
/// rechecked by the server when sent (0044). Known risk, accepted by the owner: someone who expired since the last refresh can get in.
/// </summary>
public sealed class OfflineDesk
{
    /// <summary>A copy older than this can't check anyone (D12).</summary>
    static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(30);

    sealed class State
    {
        public DateTime? UpdatedAt { get; set; }
        public List<SavedMember> Members { get; set; } = new();
        public List<QueuedCheckIn> Queue { get; set; } = new();
        public List<RefusedCheckIn> Refused { get; set; } = new();
    }

    readonly ISupabaseGateway _gw;
    readonly IDeskStore _store;
    readonly TimeProvider _clock;
    readonly State _state;

    public OfflineDesk(ISupabaseGateway gw, IDeskStore store, TimeProvider? time = null)
    {
        (_gw, _store, _clock) = (gw, store, time ?? TimeProvider.System);
        _state = Read(store);
    }

    // a damaged file must not stop the desk: it starts again from an empty list
    static State Read(IDeskStore store)
    {
        try { return store.Load() is { } text ? JsonSerializer.Deserialize<State>(text) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }
    void Write() => _store.Save(JsonSerializer.Serialize(_state));
    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public IReadOnlyList<QueuedCheckIn> Queued => _state.Queue;
    public IReadOnlyList<RefusedCheckIn> Refused => _state.Refused;
    public DateTime? UpdatedAt => _state.UpdatedAt;
    bool IsFresh => _state.UpdatedAt is { } at && Now - at <= MaxAge;
    string LastUpdated => $"Last updated at {RevenuePdf.Manila(_state.UpdatedAt ?? default)} (Manila).";
    public string OfflineText => _state.UpdatedAt is null ? "Offline, and there is no saved list on this device yet." : $"Offline. Using the saved list. {LastUpdated}";

    static SavedTerm? Covering(SavedMember m, DateTime at) => m.Terms.FirstOrDefault(t => t.Starts <= at && at < t.Ends);

    /// <summary>Saves a new copy if the current one is due, or always when asked. Returns the failure kind, or null if it worked or wasn't due.</summary>
    public async Task<AppErrorKind?> RefreshAsync(bool force = false)
    {
        if (!force && _state.UpdatedAt is { } at && Now - at < RefreshEvery) return null;
        var copy = await Safe.RunAsync(() => _gw.RpcAsync<List<SavedMember>>("desk_member_snapshot"));
        if (!copy.Ok) return copy.Error;
        (_state.Members, _state.UpdatedAt) = (copy.Value!, Now);
        Write();
        return null;
    }

    /// <summary>What the saved list says about a scanned code. Never "active": it can't know that.</summary>
    public MemberScan Find(string code)
    {
        if (!IsFresh) return new MemberScan { Result = "saved_stale" };
        var m = _state.Members.FirstOrDefault(x => x.Code == code.Trim());
        if (m is null) return new MemberScan { Result = "saved_unknown", SavedNote = LastUpdated };
        var term = Covering(m, Now);
        return new MemberScan { Result = term is null ? "saved_unpaid" : "saved", Member = m.Member, Name = m.Name, Expires = term?.Ends, SavedNote = LastUpdated };
    }

    /// <summary>Forgot phone while offline: the saved list by name. An out-of-date list finds nobody.</summary>
    public IReadOnlyList<MemberHit> Search(string fragment) => !IsFresh ? [] : _state.Members
        .Where(m => m.Name.Contains(fragment.Trim(), StringComparison.OrdinalIgnoreCase)).Take(20)
        .Select(m => Covering(m, Now) is { } term ? new MemberHit(m.Member, m.Name, true, term.Ends) : new MemberHit(m.Member, m.Name, false)).ToList();

    public void Queue(string memberId, string name)
    {
        _state.Queue.Add(new QueuedCheckIn(Guid.NewGuid(), memberId, name, Now));
        Write();
    }

    /// <summary>Sends what is queued, oldest first. A refusal is kept for staff to see; anything else stops and tries again later, so nothing is lost.</summary>
    public async Task<(int Sent, int Refused)> SendAsync()
    {
        var (sent, refused) = (0, 0);
        foreach (var item in _state.Queue.ToList())
        {
            var result = await Safe.RunAsync(() => _gw.RpcAsync<int>("record_check_in", new()
            {
                ["p_member_id"] = item.MemberId, ["p_method"] = "offline", ["p_at"] = item.AtUtc.ToString("O"), ["p_client_ref"] = item.Ref.ToString(),
            }));
            if (!result.Ok && result.Error != AppErrorKind.Validation) break;
            if (result.Ok) sent++;
            else { refused++; _state.Refused.Add(new RefusedCheckIn(item.Name, item.AtUtc, ErrorText.For(result.Error, result.Message))); }
            _state.Queue.Remove(item);
            Write();   // after each one, so a crash can't send it twice or lose the rest
        }
        return (sent, refused);
    }

    public void ClearRefused() { _state.Refused.Clear(); Write(); }
}
