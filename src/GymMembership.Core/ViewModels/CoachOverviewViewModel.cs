using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class CoachOverviewSummary(string key, string title) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    [ObservableProperty] string value = "Loading…";
    [ObservableProperty] string detail = "";
    [ObservableProperty] bool isUnavailable;
}

public partial class CoachOverviewViewModel(ICoachService coach, ISessionService sessions, IChatService chat) : LoadableViewModel
{
    public IReadOnlyList<CoachOverviewSummary> Summaries { get; } = [
        new("sessions", "Sessions"), new("requests", "Time requests"), new("trainees", "Trainees"),
        new("shifts", "My shifts"), new("attendance", "Attendance"), new("messages", "Messages"),
        new("profile", "My coach profile")];
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Local;
    [ObservableProperty] string updatedText = "Loading coach summaries…";

    public override Task OnAppearingAsync() => RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        UpdatedText = "Refreshing coach summaries…";
        try
        {
            var now = Clock().UtcDateTime;
            var today = AttendanceCalendar.LocalDay(now, Zone);
            var month = new DateOnly(today.Year, today.Month, 1);
            await Task.WhenAll(
                Load("sessions", async () =>
                {
                    var rows = Require(await sessions.SessionsAsync()).Where(r => r.IsMine && r.Session.Status != Status.Cancelled).ToList();
                    var onDay = rows.Count(r => AttendanceCalendar.LocalDay(r.Session.ScheduledStart, Zone) == today);
                    var upcoming = rows.Where(r => r.Session.Status == Status.Scheduled && r.Session.ScheduledEnd > now).OrderBy(r => r.Session.ScheduledStart).ToList();
                    var next = upcoming.FirstOrDefault();
                    return ($"{onDay} session{Plural(onDay)} today", $"{upcoming.Count} upcoming or in progress.\n" +
                        (next is null ? "No upcoming session booked." : $"Next: {next.With}\n{Range(next.Session.ScheduledStart, next.Session.ScheduledEnd)}"));
                }),
                Load("requests", async () =>
                {
                    var rows = Require(await sessions.RequestsAsync()).Rows.Where(r => r.Request.Status == Status.Pending).ToList();
                    var incoming = rows.Count(r => r.CanRespond && r.Request.RequestedBy == "member");
                    var sent = rows.Where(r => r.CanCancel && r.Request.RequestedBy == "coach").ToList();
                    var invitations = sent.Count(r => r.Request.MemberId is not null && r.Request.SessionId is null);
                    var reschedules = sent.Count(r => r.Request.SessionId is not null);
                    var open = sent.Count(r => r.Request.MemberId is null);
                    return ($"{incoming} awaiting your reply", $"{invitations} invitation{Plural(invitations)} awaiting members\n" +
                        $"{reschedules} reschedule{Plural(reschedules)} awaiting members; {open} open slot{Plural(open)}. Incoming requests include reschedules.");
                }),
                Load("trainees", async () =>
                {
                    var rows = Require(await coach.TraineesAsync());
                    var count = rows.Select(r => r.MemberId).Distinct().Count();
                    return ($"{count} active trainee{Plural(count)}", "Members with an active coaching hire. Their membership status may differ.");
                }),
                Load("shifts", async () =>
                {
                    var rows = Require(await coach.MyShiftsAsync()).Where(s => s.Status == Status.Scheduled && s.End > now).OrderBy(s => s.Start).ToList();
                    var next = rows.FirstOrDefault();
                    return ($"{rows.Count} scheduled shift{Plural(rows.Count)}", next is null ? "No upcoming shift posted." : $"Next shift (or in progress):\n{Range(next.Start, next.End)}");
                }),
                Load("attendance", async () =>
                {
                    var rows = Require(await sessions.CalendarAsync(AttendanceCalendar.StartOfDayUtc(month, Zone), AttendanceCalendar.StartOfDayUtc(month.AddMonths(1), Zone)));
                    var monthRows = AttendanceCalendar.SessionsInMonth(rows, month.Year, month.Month, Zone);
                    var summary = AttendanceCalendar.Summary(monthRows, []);
                    var rate = summary.RatePercent is { } percent ? $"{percent}% attendance" : "No attendance marked";
                    return (rate, $"{month:MMMM yyyy}: {summary.SessionsThisMonth} sessions, excluding cancellations.\n" +
                        $"{monthRows.Count(r => r.Attended)} attended, {monthRows.Count(r => r.Absent)} absent, {monthRows.Count(r => r.Excused)} excused. Rate excludes unmarked and excused sessions.");
                }),
                Load("messages", async () =>
                {
                    var rows = Require(await chat.ThreadsAsync()).Where(t => !t.IsSupport).ToList();
                    var unread = rows.Sum(t => t.Unread);
                    var conversations = rows.Count(t => t.HasUnread);
                    return ($"{unread} unread message{Plural(unread)}", $"Across {conversations} coaching conversation{Plural(conversations)}. Viewing this summary does not mark messages read.");
                }),
                Load("profile", async () =>
                {
                    var mine = Require(await coach.MyCoachAsync()) ?? throw new SummaryUnavailableException("Your coach profile could not be found.");
                    return (mine.IsAvailable ? "Accepting new trainees" : "Hiring paused",
                        $"Specialty: {(string.IsNullOrWhiteSpace(mine.Specialty) ? "Not set" : mine.Specialty)}\n" +
                        (mine.HourlyRate is { } rate ? $"Hourly rate: ₱ {rate.ToString("N2", CultureInfo.InvariantCulture)}" : "Hourly rate not set."));
                }));
            var failed = Summaries.Count(s => s.IsUnavailable);
            UpdatedText = $"{today:dddd, d MMM yyyy} · Checked {AttendanceCalendar.LocalTime(Clock().UtcDateTime, Zone):HH:mm} local time" +
                (failed == 0 ? "" : $" · {failed} summar{(failed == 1 ? "y" : "ies")} unavailable");
        }
        finally { IsBusy = false; }
    }

    async Task Load(string key, Func<Task<(string Value, string Detail)>> read)
    {
        var summary = Summaries.Single(s => s.Key == key);
        summary.Value = "Loading…";
        summary.Detail = "";
        summary.IsUnavailable = false;
        try
        {
            var result = await read();
            summary.Value = result.Value;
            summary.Detail = result.Detail;
        }
        catch (Exception ex)
        {
            summary.Value = "Unavailable";
            summary.Detail = ex is SummaryUnavailableException ? ex.Message : ErrorText.For(ErrorMapper.Map(ex));
            summary.IsUnavailable = true;
        }
    }

    string Range(DateTime from, DateTime to) => $"{AttendanceCalendar.LocalTime(from, Zone):ddd d MMM, h:mm tt} to {AttendanceCalendar.LocalTime(to, Zone):ddd d MMM, h:mm tt}";
    static string Plural(int count) => count == 1 ? "" : "s";
    static T Require<T>(AppResult<T> result) => result.Ok ? result.Value! : throw new SummaryUnavailableException(ErrorText.For(result.Error, result.Message));
    sealed class SummaryUnavailableException(string message) : Exception(message);
}
