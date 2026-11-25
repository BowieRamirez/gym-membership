using System.Net;
using System.Xml.Linq;
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Owner fixes 2026-11-25, Fix 4 Phase 4.3 (W1, W3, W5): the admin menu with words.
public class AdminMenuTests
{
    static List<XElement> Items() => XDocument.Load(Path.Combine(TestPaths.AppFolder(), "StaffShell.xaml")).Root!.Elements()
        .Where(e => e.Name.LocalName == "FlyoutItem").ToList();

    [Fact]
    public void Every_admin_and_desk_page_has_one_plain_entry_and_every_link_opens_a_real_page()
    {
        var pages = Items().Where(e => (string?)e.Attribute("ClassId") is "desk" or "admin" or "super")
            .Select(e => WebUtility.HtmlDecode((string)e.Attribute("Title")!)).Append("Overview").ToList();
        var plain = AdminMenu.Links.Where(l => l.Tab is null).Select(l => l.Page!).ToList();
        Assert.Equal(plain.Distinct().Count(), plain.Count);   // no page listed twice by its own name
        Assert.Equal(pages.OrderBy(p => p), AdminMenu.Links.Select(l => l.Page!).Distinct().OrderBy(p => p));   // every page reachable, nothing else
        var drawn = IconTests.Drawn();
        Assert.All(AdminMenu.Sections.SelectMany(s => s.Entries).SelectMany(e => (e.Children ?? []).Append(e)), e => Assert.Contains(e.Icon, drawn));
    }

    [Fact]
    public void Tree_follows_the_owners_hierarchy()
    {
        Assert.Equal(new[] { "Overview", "Workforce Management", "Finance & Payroll", "Gym" }, AdminMenu.Sections.Select(s => s.Title));
        var work = AdminMenu.Sections[1].Entries;
        Assert.Equal(new[] { "Employees", "Scheduling", "Attendance" }, work.Select(e => e.Label));
        Assert.Equal(new[] { ("Attendance Review", "review"), ("Attendance Records", "records"), ("Attendance Corrections", "corrections") },
            work[2].Children!.Select(c => (c.Label, c.Tab!)));
        var payroll = AdminMenu.Sections[2].Entries[0];
        Assert.Equal(new[] { ("Salary Configuration", "salaries"), ("Payroll Processing", "period"), ("Payslips", "payslips"), ("Payroll History", "paid_out") },
            payroll.Children!.Select(c => (c.Label, c.Tab!)));
        Assert.All(payroll.Children!, c => Assert.Equal("Payroll", c.Page));
        Assert.Equal(("Users and roles", "staff"), (work[0].Page!, work[0].Tab!));
    }

    [Fact]
    public void Payroll_and_Attendance_open_on_the_tab_the_menu_names()
    {
        var gw = new DemoGateway();
        var payroll = new PayrollViewModel(new AdminService(gw));
        payroll.OpenTab("salaries"); Assert.True(payroll.IsSalariesTab);
        payroll.OpenTab("paid_out"); Assert.True(payroll.IsPaidOutTab);
        payroll.OpenTab("payslips"); Assert.True(payroll.IsPeriodTab);
        var attendance = new AttendanceReviewViewModel(new AdminService(gw));
        attendance.OpenTab("corrections"); Assert.True(attendance.IsCorrectionsTab);
    }

    [Fact]
    public async Task Employees_shows_coaches_and_front_desk_only_and_Users_and_roles_shows_everyone()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new UsersViewModel(new AdminService(gw));
        vm.OpenTab("staff");
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.Users);
        Assert.All(vm.Users, u => Assert.True(u.Roles.Any(r => r is "coach" or "employee")));
        Assert.True(vm.StaffOnly);
        vm.OpenTab("");
        Assert.Contains(vm.Users, u => u.Roles.Contains("member"));
    }
}
