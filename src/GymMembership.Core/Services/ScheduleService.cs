using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>Everything the Scheduling page reads at once (owner fixes 2026-11-25, Fix 3).</summary>
public sealed record ScheduleData(IReadOnlyList<ScheduleTemplate> Templates, IReadOnlyList<ScheduleTemplateDay> Days,
    IReadOnlyList<ScheduleAssignment> Assignments, IReadOnlyList<ScheduleOverride> Overrides, IReadOnlyList<GymClosure> Closures,
    bool Enforced, TimeSpan Opens, TimeSpan Closes)
{
    /// <summary>The shift that applies on a day, as shift_for (0151): an override (latest wins; no times = rest), else the latest
    /// assignment's weekday (no row = rest), else none. A closed day has none. Null start = no shift.</summary>
    public (TimeSpan? Start, TimeSpan? End, ScheduleOverride? Override, bool Closed) ShiftFor(string userId, DateOnly day)
    {
        if (Closures.Any(c => c.Day == day)) return (null, null, null, true);
        if (Overrides.Where(o => o.UserId == userId && o.From <= day && day <= o.To).OrderByDescending(o => o.Id).FirstOrDefault() is { } o)
            return (o.StartsAt, o.EndsAt, o, false);
        var a = CurrentAssignment(userId, day);
        var d = a is null ? null : Days.FirstOrDefault(x => x.TemplateId == a.TemplateId && x.Weekday == (int)day.DayOfWeek switch { 0 => 7, var w => w });
        return (d?.StartsAt, d?.EndsAt, null, false);
    }

    public ScheduleAssignment? CurrentAssignment(string userId, DateOnly day) =>
        Assignments.Where(a => a.UserId == userId && a.From <= day).OrderByDescending(a => a.From).ThenByDescending(a => a.Id).FirstOrDefault();
}

public sealed class ScheduleService(ISupabaseGateway gw)
{
    public Task<AppResult<ScheduleData>> LoadAsync() => Safe.RunAsync(async () =>
    {
        var templates = gw.ListAsync<ScheduleTemplate>(); var days = gw.ListAsync<ScheduleTemplateDay>();
        var assignments = gw.ListAsync<ScheduleAssignment>(); var overrides = gw.ListAsync<ScheduleOverride>();
        var closures = gw.ListAsync<GymClosure>(); var settings = gw.ListAsync<GymSettings>();
        await Task.WhenAll(templates, days, assignments, overrides, closures, settings);
        var gym = settings.Result.FirstOrDefault() ?? new GymSettings();
        return new ScheduleData(templates.Result, days.Result, assignments.Result, overrides.Result, closures.Result,
            gym.ScheduleEnforced, gym.OpensAt, gym.ClosesAt);
    });

    static string T(TimeSpan t) => t.ToString(@"hh\:mm");

    /// <summary>A null id makes a new template. Days are (ISO weekday, start, end); a missing weekday is a rest day.</summary>
    public Task<AppResult<ScheduleSaveResult>> SaveTemplateAsync(int? id, string name, IEnumerable<(int Weekday, TimeSpan Start, TimeSpan End)> days) =>
        Safe.RunAsync(async () => await gw.RpcAsync<ScheduleSaveResult>("schedule_template_save", new()
        {
            ["p_id"] = id!, ["p_name"] = name,
            ["p_days"] = days.Select(d => new Dictionary<string, object> { ["weekday"] = d.Weekday, ["starts"] = T(d.Start), ["ends"] = T(d.End) }).ToList()
        }) ?? new ScheduleSaveResult());

    public Task<AppResult<int>> DuplicateTemplateAsync(int id, string name) =>
        Safe.RunAsync(() => gw.RpcAsync<int>("schedule_template_duplicate", new() { ["p_id"] = id, ["p_name"] = name }));

    public Task<AppResult<bool>> ArchiveTemplateAsync(int id) =>
        Safe.RunAsync(async () => { await gw.RpcAsync("schedule_template_archive", new() { ["p_id"] = id }); return true; });

    /// <summary>Returns the days whose coach shift was kept because a session is booked in it (S8).</summary>
    public Task<AppResult<List<string>>> AssignAsync(string userId, int templateId, DateOnly from) =>
        Safe.RunAsync(async () => await gw.RpcAsync<List<string>>("schedule_assign", new()
            { ["p_user"] = userId, ["p_template"] = templateId, ["p_from"] = from.ToString("yyyy-MM-dd") }) ?? []);

    /// <summary>No times = a rest day (or days).</summary>
    public Task<AppResult<List<string>>> AddOverrideAsync(string userId, DateOnly from, DateOnly to, TimeSpan? start, TimeSpan? end, string reason) =>
        Safe.RunAsync(async () => await gw.RpcAsync<List<string>>("schedule_override_add", new()
        {
            ["p_user"] = userId, ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd"),
            ["p_start"] = start is { } s ? T(s) : null!, ["p_end"] = end is { } e ? T(e) : null!, ["p_reason"] = reason
        }) ?? []);

    public Task<AppResult<List<string>>> RemoveOverrideAsync(int id) =>
        Safe.RunAsync(async () => await gw.RpcAsync<List<string>>("schedule_override_remove", new() { ["p_id"] = id }) ?? []);

    public Task<AppResult<bool>> SetEnforcedAsync(bool on) =>
        Safe.RunAsync(async () => { await gw.RpcAsync("schedule_set_enforced", new() { ["p_on"] = on }); return on; });
}
