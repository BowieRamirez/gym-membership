using System.Text.RegularExpressions;
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

// Phase 8, items 5 and 14: readable labels, 10 rows a page from the server, filters and a safe CSV export
public class AuditLogTests
{
    static string Root => Path.GetFullPath(Path.Combine(TestPaths.AppFolder(), "..", ".."));

    /// <summary>Every action code and object table the system writes: migrations, Edge Functions and the demo.</summary>
    static (HashSet<string> Actions, HashSet<string> Entities) Written()
    {
        var (actions, entities) = (new HashSet<string>(), new HashSet<string>());
        foreach (var f in Directory.GetFiles(Path.Combine(Root, "supabase", "migrations"), "*.sql"))
        {
            var sql = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(sql, @"audit\(\s*'([a-z_]+\.[a-z_]+)'\s*,\s*'([a-z_]+)'")) { actions.Add(m.Groups[1].Value); entities.Add(m.Groups[2].Value); }
            foreach (Match m in Regex.Matches(sql, @"audit\(\s*case when [^']+'([a-z_]+\.[a-z_]+)' else '([a-z_]+\.[a-z_]+)' end\s*,\s*'([a-z_]+)'")) { actions.Add(m.Groups[1].Value); actions.Add(m.Groups[2].Value); entities.Add(m.Groups[3].Value); }
            // archive_catalog_record audits under the table it picked (0024)
            foreach (Match table in Regex.Matches(sql, @"v_table := case[^;]+;"))
                foreach (Match m in Regex.Matches(table.Value, @"then '([a-z_]+)'")) entities.Add(m.Groups[1].Value);
        }
        foreach (var f in Directory.GetFiles(Path.Combine(Root, "supabase", "functions"), "index.ts", SearchOption.AllDirectories))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"action: '([a-z_.]+)', entity: '([a-z_]+)'")) { actions.Add(m.Groups[1].Value); entities.Add(m.Groups[2].Value); }
        foreach (var f in Directory.GetFiles(Path.Combine(Root, "src", "GymMembership.Core", "Demo"), "*.cs"))
        {
            var cs = File.ReadAllText(f);
            foreach (Match m in Regex.Matches(cs, @"Audit\(""([a-z_.]+)"", ""([a-z_]+)""")) { actions.Add(m.Groups[1].Value); entities.Add(m.Groups[2].Value); }
            foreach (Match m in Regex.Matches(cs, @"Action = ""([a-z_]+\.[a-z_]+)""")) actions.Add(m.Groups[1].Value);
            foreach (Match m in Regex.Matches(cs, @"""(user\.[a-z_]+)"" : ""(user\.[a-z_]+)""")) { actions.Add(m.Groups[1].Value); actions.Add(m.Groups[2].Value); }
        }
        return (actions, entities);
    }

    [Fact]
    public void Every_action_and_object_the_system_writes_has_a_readable_label()
    {
        var (actions, entities) = Written();
        Assert.True(actions.Count >= 24, $"the scan found only {actions.Count} codes: {string.Join(", ", actions)}");
        Assert.Contains("user.deactivate", actions);
        Assert.Empty(actions.Where(a => !AuditLabels.Actions.ContainsKey(a)));
        Assert.Empty(actions.Where(a => !AuditLabels.Areas.ContainsKey(a.Split('.')[0])));
        Assert.Empty(entities.Where(e => !AuditLabels.Objects.ContainsKey(e)));
        Assert.Contains("membership_packages", entities);
    }

    [Fact]
    public void Labels_read_plainly_and_an_unknown_code_shows_as_itself()
    {
        Assert.Equal("Account deleted", AuditLabels.Action("user.archive"));
        Assert.Equal(("Account", "Account"), (AuditLabels.Area("user.archive"), AuditLabels.Object("profiles")));
        Assert.Equal(("brand.new", "brand", "brand_new"), (AuditLabels.Action("brand.new"), AuditLabels.Area("brand.new"), AuditLabels.Object("brand_new")));
        Assert.Equal("Reason: Duplicate; Approved: True", AuditLabels.Details(new Dictionary<string, object> { ["reason"] = "Duplicate", ["approved"] = true }));
        Assert.Equal("", AuditLabels.Details(null));
        var row = new AuditRecord { Action = "user.archive", Entity = "profiles", EntityId = "u-1", ObjectName = "Alex Tan", Role = "subadmin", CreatedAt = new DateTime(2026, 11, 6, 16, 30, 0, DateTimeKind.Utc) };
        Assert.Equal(("2026-11-07 00:30", "Admin", "Alex Tan"), (row.WhenText, row.RoleText, row.ObjectText));
        Assert.Equal("", new AuditRecord().RoleText);   // D7: older entries stay blank, never guessed
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-2", "\"'-2\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tx", "\"'\tx\"")]
    [InlineData("Dana, Reyes", "\"Dana, Reyes\"")]
    [InlineData("", "\"\"")]
    public void A_csv_cell_is_quoted_and_never_runs_as_a_formula(string value, string cell) => Assert.Equal(cell, AuditCsv.Cell(value));

    [Fact]
    public void The_csv_has_the_table_columns_and_one_line_per_row()
    {
        var csv = AuditCsv.Build([new AuditRecord { Action = "user.archive", Entity = "profiles", ActorName = "Dana Reyes", Role = "admin", IpAddress = "203.0.113.7", ObjectName = "Alex Tan",
            Details = new() { ["reason"] = "=cmd" }, CreatedAt = new DateTime(2026, 11, 6, 1, 0, 0, DateTimeKind.Utc) }]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("\"Date and time (Manila)\",\"User\",\"Role\",\"IP address\",\"Action type\",\"Action\",\"Object type\",\"Object\",\"Change details\"", lines[0]);
        Assert.Equal("\"2026-11-06 09:00\",\"Dana Reyes\",\"System admin\",\"203.0.113.7\",\"Account\",\"Account deleted\",\"Account\",\"Alex Tan\",\"Reason: =cmd\"", lines[1]);
    }

    static async Task<(DemoGateway Gw, AuditViewModel Vm)> Signed(string email = "admin@demo.io")
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new AuditViewModel(new AdminService(gw)));
    }

    [Fact]
    public async Task The_log_shows_ten_a_page_newest_first_with_the_actors_role()
    {
        var (gw, vm) = await Signed();
        var admin = new AdminService(gw);
        for (var i = 0; i < 12; i++) Assert.True((await admin.SetActiveAsync("u-alex", i % 2 == 1)).Ok);
        await vm.OnAppearingAsync();
        Assert.Equal(10, vm.Entries.Count);
        Assert.True(vm.HasMore);
        Assert.Equal(("Account reactivated", "Dana Reyes", "System admin"), (vm.Entries[0].ActionText, vm.Entries[0].ActorName, vm.Entries[0].RoleText));
        Assert.Equal("Alex Tan", vm.Entries[0].ObjectText);
        Assert.True(vm.Entries.Zip(vm.Entries.Skip(1)).All(p => p.First.CreatedAt >= p.Second.CreatedAt));
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(("Page 2", false), (vm.PageText, vm.HasMore));
        Assert.Equal(4, vm.Entries.Count);   // 12 + the two seeded entries
        await vm.PreviousCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Entries.Count);
    }

    [Fact]
    public async Task Filters_narrow_the_log_and_clearing_brings_everything_back()
    {
        var (gw, vm) = await Signed();
        Assert.True((await new AdminService(gw).SetActiveAsync("u-alex", false)).Ok);
        await vm.OnAppearingAsync();
        vm.AreaIndex = Array.IndexOf(vm.Areas, "Payment");
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.All(vm.Entries, e => Assert.Equal("Payment", e.AreaText));
        Assert.NotEmpty(vm.Entries);
        vm.AreaIndex = 0; vm.ObjectIndex = Array.IndexOf(vm.ObjectTypes, "Account"); vm.User = "dana";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("Account deactivated", Assert.Single(vm.Entries).ActionText);
        vm.User = "nobody"; await vm.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.IsEmpty);
        (vm.UseDates, vm.From, vm.To) = (true, DateTime.Today, DateTime.Today.AddDays(-1));
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("The end date must be on or after the start date.", vm.ErrorMessage);
        await vm.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Entries.Count);
    }

    [Fact]
    public async Task Export_saves_every_filtered_row_not_just_the_page()
    {
        var (gw, vm) = await Signed();
        for (var i = 0; i < 14; i++) Assert.True((await new AdminService(gw).SetActiveAsync("u-alex", i % 2 == 1)).Ok);
        string? saved = null, name = null;
        vm.SaveFile = (file, text) => { (name, saved) = (file, text); return Task.FromResult<string?>("Downloads"); };
        await vm.OnAppearingAsync();
        vm.ObjectIndex = Array.IndexOf(vm.ObjectTypes, "Account");
        await vm.ApplyCommand.ExecuteAsync(null);
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.StartsWith("audit-log-", name);
        Assert.Equal(15, saved!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);   // header + 14
        Assert.Equal("Saved 14 entries to Downloads.", vm.Notice);
        vm.SaveFile = (_, _) => Task.FromResult<string?>(null);   // cancelled
        vm.Notice = null;
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task Only_staff_with_audit_read_see_the_log_and_bad_filters_are_refused()
    {
        var (_, vm) = await Signed("subadmin@demo.io");
        await vm.OnAppearingAsync();
        Assert.Empty(vm.Entries);
        Assert.NotNull(vm.ErrorMessage);
        var (gw, _) = await Signed();
        var admin = new AdminService(gw);
        Assert.False((await admin.AuditPageAsync(new(new DateOnly(2026, 2, 1), new DateOnly(2026, 1, 1)), 0)).Ok);
        Assert.False((await admin.AuditPageAsync(new(User: new string('x', 101)), 0)).Ok);
    }
}
