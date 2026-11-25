using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner fixes 2026-11-25, Fix 3 Phase 3.3: the Scheduling page (templates, people, week, changes, the check-in switch).
public class SchedulingViewModelTests
{
    static async Task<(DemoGateway Gw, SchedulingViewModel Vm)> Open()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new SchedulingViewModel(new AdminService(gw), new ScheduleService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    static async Task SaveWeekdays(SchedulingViewModel vm, string name = "Weekdays")
    {
        vm.NewTemplateCommand.Execute(null);
        vm.TemplateName = name;
        foreach (var d in vm.EditorDays) { d.Works = d.Weekday <= 5; d.Start = "9:00 AM"; d.End = "5:00 PM"; }
        await vm.SaveTemplateCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task A_template_is_saved_from_the_week_editor_and_summed_up_in_words()
    {
        var (_, vm) = await Open();
        await SaveWeekdays(vm);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsEditingTemplate);
        var t = Assert.Single(vm.Templates);
        Assert.Equal("Weekdays", t.Name);
        Assert.Equal("Mon–Fri 9:00 AM – 5:00 PM", t.SummaryText);
        Assert.Contains("9:00 AM", vm.TimeOptions);
        Assert.DoesNotContain("9:15 AM", vm.TimeOptions);   // the half-hour grid
    }

    [Fact]
    public async Task Assigning_a_person_shows_their_template_and_their_shifts_in_the_week()
    {
        var (_, vm) = await Open();
        await SaveWeekdays(vm);
        var person = vm.People.First(p => p.RoleLabel == "Front desk");
        person.BeginChangeCommand.Execute(null);
        person.TemplateChoice = vm.Templates[0].Name;
        await person.SaveCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        person = vm.People.First(p => p.UserId == person.UserId);
        Assert.StartsWith("Weekdays from ", person.CurrentText);

        vm.NextWeekCommand.Execute(null);   // all of next week falls after today
        var row = vm.WeekRows.First(r => r.UserId == person.UserId);
        Assert.Equal("9:00 AM – 5:00 PM", row.Days[0].Text);   // Monday
        Assert.Equal("Rest", row.Days[6].Text);                // Sunday
        Assert.Contains(vm.WeekRows, r => r.Days.All(d => d.Text == "No schedule"));
    }

    [Fact]
    public async Task A_change_for_a_day_shows_in_the_week_and_can_be_removed()
    {
        var (_, vm) = await Open();
        await SaveWeekdays(vm);
        var person = vm.People.First(p => p.RoleLabel == "Front desk");
        person.BeginChangeCommand.Execute(null);
        person.TemplateChoice = "Weekdays";
        await person.SaveCommand.ExecuteAsync(null);

        vm.NextWeekCommand.Execute(null);
        var tuesday = vm.WeekStart.AddDays(1);
        vm.OverridePerson = vm.People.First(p => p.UserId == person.UserId);
        vm.OverrideFrom = tuesday.ToDateTime(TimeOnly.MinValue); vm.OverrideTo = vm.OverrideFrom;
        vm.OverrideWorks = false; vm.OverrideReason = "Sick";
        await vm.AddOverrideCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        var cell = vm.WeekRows.First(r => r.UserId == person.UserId).Days[1];
        Assert.Equal(("Rest", true, "Sick"), (cell.Text, cell.IsChange, cell.Reason));

        var change = Assert.Single(vm.Changes);
        await change.RemoveCommand.ExecuteAsync(null);
        Assert.Empty(vm.Changes);
        Assert.Equal("9:00 AM – 5:00 PM", vm.WeekRows.First(r => r.UserId == person.UserId).Days[1].Text);
    }

    [Fact]
    public async Task Turning_on_shift_check_in_names_everyone_who_has_no_schedule_first()
    {
        var (_, vm) = await Open();
        string? asked = null;
        Confirm.Answer = (title, message, _) => { asked = message; return Task.FromResult(true); };
        try
        {
            Assert.False(vm.IsEnforced);
            await vm.ToggleEnforcedCommand.ExecuteAsync(null);
            Assert.True(vm.IsEnforced);
            Assert.NotNull(asked);
            Assert.Contains(vm.People.First().Name, asked);
            Assert.Contains("can't check in", asked);
            await vm.ToggleEnforcedCommand.ExecuteAsync(null);
            Assert.False(vm.IsEnforced);
        }
        finally { Confirm.Answer = null; }
    }

    [Fact]
    public async Task An_archived_template_leaves_the_list_and_one_in_use_cannot_be_archived()
    {
        var (_, vm) = await Open();
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        try
        {
            await SaveWeekdays(vm);
            var person = vm.People.First();
            person.BeginChangeCommand.Execute(null);
            person.TemplateChoice = "Weekdays";
            await person.SaveCommand.ExecuteAsync(null);
            await vm.Templates[0].ArchiveCommand.ExecuteAsync(null);
            Assert.Contains("works on this template", vm.ErrorMessage);

            await vm.Templates[0].DuplicateCommand.ExecuteAsync(null);
            var copy = vm.Templates.Single(t => t.Name != "Weekdays");
            Assert.Equal("Weekdays (copy)", copy.Name);
            await copy.ArchiveCommand.ExecuteAsync(null);
            Assert.DoesNotContain(vm.Templates, t => t.Name == "Weekdays (copy)");
        }
        finally { Confirm.Answer = null; }
    }
}
