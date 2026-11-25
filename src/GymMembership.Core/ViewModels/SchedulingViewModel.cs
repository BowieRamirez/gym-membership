using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

// Owner fixes 2026-11-25, Fix 3 Phase 3.3: the admin's Scheduling page. Week (who works when, plus one-off changes),
// Templates (a Mon-Sun week of shifts), People (who follows which template from when), and the shift check-in switch.

public sealed record ScheduleDayCell(string Text, bool IsRest, bool IsChange, string? Reason);
public sealed record ScheduleWeekRow(string UserId, string Name, string RoleLabel, IReadOnlyList<ScheduleDayCell> Days)
{
    /// <summary>The narrow-window card: one day a line.</summary>
    public string DaysText => string.Join("\n", Days.Select((d, i) => $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)((i + 1) % 7))}  {d.Text}{(d.Reason is { } r ? $" ({r})" : "")}"));
}

public sealed partial class TemplateDayInput(int weekday, IReadOnlyList<string>? options = null) : ObservableObject
{
    public int Weekday { get; } = weekday;   // ISO: 1 Monday .. 7 Sunday
    public IReadOnlyList<string> Options { get; } = options ?? [];
    public string DayName => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)(Weekday % 7));
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WorksText))] private bool works;
    /// <summary>The day tile's toggle chip (chosen = works).</summary>
    public string WorksText => Works ? "Works" : "Day off";
    [CommunityToolkit.Mvvm.Input.RelayCommand] private void ToggleWorks() => Works = !Works;
    [ObservableProperty] private string start = "9:00 AM";
    [ObservableProperty] private string end = "5:00 PM";
}

public sealed partial class ScheduleTemplateItem(ScheduleTemplate t, string summary, SchedulingViewModel page) : ObservableObject
{
    public ScheduleTemplate Template { get; } = t;
    public string Name => Template.Name;
    public string SummaryText { get; } = summary;
    [RelayCommand] private void Edit() => page.BeginEdit(this);
    [RelayCommand] private Task DuplicateAsync() => page.DuplicateAsync(this);
    [RelayCommand] private Task ArchiveAsync() => page.ArchiveAsync(this);
}

public sealed partial class SchedulePersonItem(StaffSalary s, string current, string? next, SchedulingViewModel page) : ObservableObject
{
    public string UserId => s.UserId;
    public string Name => s.FullName;
    public string RoleLabel => s.RoleLabel;
    public string CurrentText { get; } = current;
    public string? NextText { get; } = next;
    public bool HasNext => NextText is not null;
    public IReadOnlyList<string> TemplateNames => page.TemplateNames;
    [ObservableProperty] private bool isChanging;
    [ObservableProperty] private string? templateChoice;
    [ObservableProperty] private DateTime fromDate = SchedulingViewModel.Today.ToDateTime(TimeOnly.MinValue);
    public DateTime MinDate => SchedulingViewModel.Today.ToDateTime(TimeOnly.MinValue);
    [RelayCommand] private void BeginChange() { TemplateChoice ??= TemplateNames.FirstOrDefault(); IsChanging = true; }
    [RelayCommand] private void CancelChange() => IsChanging = false;
    [RelayCommand] private Task SaveAsync() => page.AssignAsync(this);
}

public sealed partial class ScheduleChangeItem(ScheduleOverride o, string who, SchedulingViewModel page) : ObservableObject
{
    public ScheduleOverride Override { get; } = o;
    public string Who { get; } = who;
    public string WhenText => (Override.From == Override.To ? SchedulingViewModel.DayText(Override.From)
        : $"{SchedulingViewModel.DayText(Override.From)} – {SchedulingViewModel.DayText(Override.To)}")
        + " · " + (Override.StartsAt is { } s && Override.EndsAt is { } e ? SchedulingViewModel.Range(s, e) : "Rest");
    public string Reason => Override.Reason;
    public bool CanRemove => Override.From > SchedulingViewModel.Today;   // a change that has started stays, so past days stay explainable
    [RelayCommand] private Task RemoveAsync() => page.RemoveChangeAsync(this);
}

public partial class SchedulingViewModel(IAdminService admin, ScheduleService schedule) : LoadableViewModel
{
    internal static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));   // Manila
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    internal static string Clock(TimeSpan t) => DateTime.Today.Add(t).ToString("h:mm tt", Inv);
    internal static string Range(TimeSpan s, TimeSpan e) => $"{Clock(s)} – {Clock(e)}";
    internal static string DayText(DateOnly d) => d.ToString("ddd d MMM", Inv);
    static TimeSpan? Parse(string? s) => DateTime.TryParseExact(s, "h:mm tt", Inv, DateTimeStyles.None, out var t) ? t.TimeOfDay : null;

    ScheduleData? data;
    IReadOnlyList<StaffSalary> staff = [];

    // ---- tabs ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWeekTab), nameof(IsTemplatesTab), nameof(IsPeopleTab))] private string tab = "week";
    public bool IsWeekTab => Tab == "week";
    public bool IsTemplatesTab => Tab == "templates";
    public bool IsPeopleTab => Tab == "people";
    [RelayCommand] private void OpenWeekTab() => Tab = "week";
    [RelayCommand] private void OpenTemplatesTab() => Tab = "templates";
    [RelayCommand] private void OpenPeopleTab() => Tab = "people";

    public ObservableCollection<ScheduleTemplateItem> Templates { get; } = new();
    public ObservableCollection<SchedulePersonItem> People { get; } = new();
    public ObservableCollection<ScheduleWeekRow> WeekRows { get; } = new();
    public ObservableCollection<ScheduleChangeItem> Changes { get; } = new();
    public IReadOnlyList<string> TemplateNames => Templates.Select(t => t.Name).ToList();
    /// <summary>Start and end choices: every half hour inside gym hours (shifts sit on the half hour, 0028).</summary>
    public ObservableCollection<string> TimeOptions { get; } = new();

    public override Task OnAppearingAsync() => LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        IReadOnlyList<StaffSalary>? people = null;
        if (!await RunAsync(admin.PayrollStaffAsync, p => people = p)) return;
        if (!await RunAsync(schedule.LoadAsync, d => data = d)) return;
        staff = people!.Where(p => p.Role is "coach" or "employee").OrderBy(p => p.Role).ThenBy(p => p.FullName).ToList();
        Rebuild();
    }

    void Rebuild()
    {
        if (data is not { } d) return;
        IsEnforced = d.Enforced;
        var opts = new List<string>();
        for (var t = d.Opens; t <= d.Closes && t < TimeSpan.FromHours(24); t += TimeSpan.FromMinutes(30)) opts.Add(Clock(t));
        if (d.Closes == TimeSpan.FromHours(24)) opts.Add(Clock(TimeSpan.Zero));   // ponytail: a gym open until midnight; rare, so no special label
        Fill(TimeOptions, opts);
        Fill(Templates, d.Templates.Where(t => t.ArchivedAt is null).OrderBy(t => t.Name)
            .Select(t => new ScheduleTemplateItem(t, Summary(d.Days.Where(x => x.TemplateId == t.Id)), this)));
        OnPropertyChanged(nameof(TemplateNames));
        string Name(int id) => d.Templates.FirstOrDefault(t => t.Id == id)?.Name ?? "Template";
        Fill(People, staff.Select(s =>
        {
            var cur = d.CurrentAssignment(s.UserId, Today);
            var next = d.Assignments.Where(a => a.UserId == s.UserId && a.From > Today).OrderBy(a => a.From).FirstOrDefault();
            return new SchedulePersonItem(s, cur is null ? "No schedule" : $"{Name(cur.TemplateId)} from {DayText(cur.From)}",
                next is null ? null : $"{Name(next.TemplateId)} from {DayText(next.From)}", this);
        }));
        string Who(string id) => staff.FirstOrDefault(s => s.UserId == id)?.FullName ?? "Someone";
        Fill(Changes, d.Overrides.Where(o => o.To >= Today).OrderBy(o => o.From).ThenBy(o => o.Id).Select(o => new ScheduleChangeItem(o, Who(o.UserId), this)));
        OnPropertyChanged(nameof(HasChanges));
        BuildWeek();
    }

    /// <summary>"Mon–Fri 9:00 AM – 5:00 PM · Sat 9:00 AM – 1:00 PM": runs of days with the same hours; rest days are left out.</summary>
    internal static string Summary(IEnumerable<ScheduleTemplateDay> days)
    {
        var byDay = days.ToDictionary(d => d.Weekday);
        var parts = new List<string>();
        string Short(int w) => Inv.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(w % 7));
        for (var w = 1; w <= 7; w++)
        {
            if (!byDay.TryGetValue(w, out var first)) continue;
            var last = w;
            while (last < 7 && byDay.TryGetValue(last + 1, out var n) && n.StartsAt == first.StartsAt && n.EndsAt == first.EndsAt) last++;
            parts.Add($"{Short(w)}{(last > w ? "–" + Short(last) : "")} {Range(first.StartsAt, first.EndsAt)}");
            w = last;
        }
        return parts.Count == 0 ? "Rest every day" : string.Join(" · ", parts);
    }

    // ---- week ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(WeekLabel), nameof(DayHeaders))]
    private DateOnly weekStart = Today.AddDays(-(((int)Today.DayOfWeek + 6) % 7));   // Monday
    public string WeekLabel => $"{WeekStart.ToString("d MMM", Inv)} – {WeekStart.AddDays(6).ToString("d MMM yyyy", Inv)}";
    public IReadOnlyList<string> DayHeaders => Enumerable.Range(0, 7).Select(i => WeekStart.AddDays(i).ToString("ddd d", Inv)).ToList();
    [RelayCommand] private void PrevWeek() { WeekStart = WeekStart.AddDays(-7); BuildWeek(); }
    [RelayCommand] private void NextWeek() { WeekStart = WeekStart.AddDays(7); BuildWeek(); }

    void BuildWeek()
    {
        if (data is not { } d) return;
        Fill(WeekRows, staff.Select(s =>
        {
            var hasAny = d.Assignments.Any(a => a.UserId == s.UserId) || d.Overrides.Any(o => o.UserId == s.UserId);
            return new ScheduleWeekRow(s.UserId, s.FullName, s.RoleLabel, Enumerable.Range(0, 7).Select(i =>
            {
                var (st, en, ov, closed) = d.ShiftFor(s.UserId, WeekStart.AddDays(i));
                if (closed) return new ScheduleDayCell("Gym closed", true, false, null);
                if (!hasAny) return new ScheduleDayCell("No schedule", true, false, null);
                return st is { } a && en is { } b ? new ScheduleDayCell(Range(a, b), false, ov is not null, ov?.Reason)
                                                  : new ScheduleDayCell("Rest", true, ov is not null, ov?.Reason);
            }).ToList());
        }));
    }

    // ---- templates ----
    int? editingId;
    [ObservableProperty] private bool isEditingTemplate;
    [ObservableProperty] private string templateName = "";
    public ObservableCollection<TemplateDayInput> EditorDays { get; } = new();
    public string EditorTitle => editingId is null ? "New template" : "Edit template";

    [RelayCommand]
    private void NewTemplate()
    {
        editingId = null; TemplateName = "";
        Fill(EditorDays, Enumerable.Range(1, 7).Select(w => new TemplateDayInput(w, TimeOptions.ToList()) { Works = w <= 5 }));
        OnPropertyChanged(nameof(EditorTitle)); IsEditingTemplate = true; Tab = "templates";
    }

    internal void BeginEdit(ScheduleTemplateItem item)
    {
        editingId = item.Template.Id; TemplateName = item.Name;
        var days = data?.Days.Where(d => d.TemplateId == editingId).ToDictionary(d => d.Weekday) ?? new();
        Fill(EditorDays, Enumerable.Range(1, 7).Select(w => days.TryGetValue(w, out var d)
            ? new TemplateDayInput(w, TimeOptions.ToList()) { Works = true, Start = Clock(d.StartsAt), End = Clock(d.EndsAt) }
            : new TemplateDayInput(w, TimeOptions.ToList())));
        OnPropertyChanged(nameof(EditorTitle)); IsEditingTemplate = true;
    }

    [RelayCommand] private void CancelTemplate() => IsEditingTemplate = false;

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        Notice = null;
        if (string.IsNullOrWhiteSpace(TemplateName)) { ErrorMessage = "Give the template a name, like Weekdays 9-5."; return; }
        var days = new List<(int, TimeSpan, TimeSpan)>();
        foreach (var d in EditorDays.Where(d => d.Works))
        {
            if (Parse(d.Start) is not { } s || Parse(d.End) is not { } e || e <= s) { ErrorMessage = $"{d.DayName}: the shift must end after it starts."; return; }
            days.Add((d.Weekday, s, e));
        }
        ScheduleSaveResult? saved = null;
        if (!await RunAsync(() => schedule.SaveTemplateAsync(editingId, TemplateName.Trim(), days), r => saved = r)) { NameTaken(); return; }
        IsEditingTemplate = false;
        await LoadAsync();
        Notice = Kept($"Template {TemplateName.Trim()} saved.", saved!.Kept);
    }

    void NameTaken() { if (ErrorMessage is not null && ErrorMessage.Contains("already has that name")) ErrorMessage = "Another template already has that name. Choose a different one."; }

    internal async Task DuplicateAsync(ScheduleTemplateItem item)
    {
        Notice = null;
        var name = $"{item.Name} (copy)";
        for (var n = 2; Templates.Any(t => t.Name == name) || (data?.Templates.Any(t => t.Name == name) ?? false); n++) name = $"{item.Name} (copy {n})";
        if (!await RunAsync(() => schedule.DuplicateTemplateAsync(item.Template.Id, name), _ => { })) { NameTaken(); return; }
        await LoadAsync();
        Notice = $"Copied as {name}. Edit it to change the hours.";
    }

    internal async Task ArchiveAsync(ScheduleTemplateItem item)
    {
        Notice = null;
        if (!await Confirm.AskAsync($"Archive {item.Name}?", "It leaves the list. Past days that used it keep their hours.", "Archive")) return;
        if (!await RunAsync(() => schedule.ArchiveTemplateAsync(item.Template.Id))) return;
        await LoadAsync();
        Notice = $"{item.Name} archived.";
    }

    // ---- people ----
    internal async Task AssignAsync(SchedulePersonItem person)
    {
        Notice = null;
        if (Templates.FirstOrDefault(t => t.Name == person.TemplateChoice) is not { } tpl) { ErrorMessage = "Choose a template."; return; }
        var from = DateOnly.FromDateTime(person.FromDate);
        List<string>? kept = null;
        if (!await RunAsync(() => schedule.AssignAsync(person.UserId, tpl.Template.Id, from), k => kept = k)) return;
        await LoadAsync();
        Notice = Kept($"{person.Name} follows {tpl.Name} from {DayText(from)}.", kept!);
    }

    // ---- one-off changes ----
    [ObservableProperty] private SchedulePersonItem? overridePerson;
    [ObservableProperty] private DateTime overrideFrom = Today.AddDays(1).ToDateTime(TimeOnly.MinValue);
    [ObservableProperty] private DateTime overrideTo = Today.AddDays(1).ToDateTime(TimeOnly.MinValue);
    [ObservableProperty] private bool overrideWorks;
    [ObservableProperty] private string overrideStart = "9:00 AM";
    [ObservableProperty] private string overrideEnd = "5:00 PM";
    [ObservableProperty] private string overrideReason = "";
    public DateTime MinDate => Today.ToDateTime(TimeOnly.MinValue);
    public bool HasChanges => Changes.Count > 0;
    partial void OnOverrideFromChanged(DateTime value) { if (OverrideTo < value) OverrideTo = value; }

    [RelayCommand]
    private async Task AddOverrideAsync()
    {
        Notice = null;
        if (OverridePerson is not { } who) { ErrorMessage = "Choose who the change is for."; return; }
        if (string.IsNullOrWhiteSpace(OverrideReason)) { ErrorMessage = "Say why, like Sick or Covering for Ana."; return; }
        TimeSpan? s = null, e = null;
        if (OverrideWorks && (Parse(OverrideStart) is not { } a || Parse(OverrideEnd) is not { } b || b <= a)) { ErrorMessage = "The shift must end after it starts."; return; }
        if (OverrideWorks) (s, e) = (Parse(OverrideStart), Parse(OverrideEnd));
        var (from, to) = (DateOnly.FromDateTime(OverrideFrom), DateOnly.FromDateTime(OverrideTo));
        List<string>? kept = null;
        if (!await RunAsync(() => schedule.AddOverrideAsync(who.UserId, from, to, s, e, OverrideReason.Trim()), k => kept = k)) return;
        OverrideReason = "";
        await LoadAsync();
        Notice = Kept($"Change added for {who.Name}.", kept!);
    }

    internal async Task RemoveChangeAsync(ScheduleChangeItem item)
    {
        Notice = null;
        if (!await RunAsync(() => schedule.RemoveOverrideAsync(item.Override.Id), _ => { })) return;
        await LoadAsync();
        Notice = $"Change for {item.Who} removed.";
    }

    // S8: coach shifts with a booked session inside are kept, and the admin is told which days
    static string Kept(string done, IReadOnlyList<string> kept) => kept.Count == 0 ? done
        : $"{done} {(kept.Count == 1 ? "1 coach shift was" : $"{kept.Count} coach shifts were")} kept because sessions are booked in it: "
          + string.Join(", ", kept.Select(k => DayText(DateOnly.Parse(k, Inv)))) + ". Move those sessions to another coach first.";

    // ---- the check-in switch ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EnforcedTitle), nameof(EnforcedText), nameof(EnforcedButton))] private bool isEnforced;
    public string EnforcedTitle => IsEnforced ? "Shift check-in is on" : "Shift check-in is off";
    public string EnforcedText => IsEnforced
        ? "Coaches and front desk check in only on a day they have a shift, from 5 minutes before it starts."
        : "Anyone can check in while the gym is open. Set everyone's schedule, then turn this on.";
    public string EnforcedButton => IsEnforced ? "Turn off" : "Turn on";

    [RelayCommand]
    private async Task ToggleEnforcedAsync()
    {
        Notice = null;
        var on = !IsEnforced;
        if (on)
        {
            var missing = staff.Where(s => data is null || !data.Assignments.Any(a => a.UserId == s.UserId)).Select(s => s.FullName).ToList();
            var body = missing.Count == 0 ? "Everyone has a schedule. From now on, check-in follows each person's shift."
                : $"No schedule yet: {string.Join(", ", missing)}. They can't check in until they have one.";
            if (!await Confirm.AskAsync("Turn on shift check-in?", body, "Turn on")) return;
        }
        else if (!await Confirm.AskAsync("Turn off shift check-in?", "Anyone will be able to check in while the gym is open.", "Turn off")) return;
        if (!await RunAsync(() => schedule.SetEnforcedAsync(on))) return;
        IsEnforced = on;
        Notice = on ? "Shift check-in is on." : "Shift check-in is off.";
    }
}
