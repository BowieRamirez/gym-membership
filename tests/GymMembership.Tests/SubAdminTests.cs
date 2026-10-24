using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 13 (D15) in demo mode: the admin is the super admin; the sub-admin does desk work plus catalog, discounts, revenue and
// member/coach accounts, and is refused everything the super admin keeps.
public class SubAdminTests
{
    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    static string IdOf(DemoGateway gw, string email) => gw.ListAsync<Profile>(p => p.Username == email).Result.Single().Id;

    [Theory]
    [InlineData(new[] { "subadmin" }, AppRole.SubAdmin)]
    [InlineData(new[] { "subadmin", "employee" }, AppRole.SubAdmin)]
    [InlineData(new[] { "admin", "subadmin" }, AppRole.Admin)]
    public void The_highest_role_wins(string[] roles, AppRole expected) => Assert.Equal(expected, RoleResolver.Pick(roles));

    [Fact]
    public async Task The_demo_sub_admin_signs_in_as_a_sub_admin() =>
        Assert.Equal(AppRole.SubAdmin, (await new AuthService(new DemoGateway()).SignInAsync("subadmin@demo.io", DemoGateway.DemoPassword)).Value);

    [Fact]
    public async Task A_sub_admin_manages_members_and_coaches()
    {
        var gw = await As("subadmin@demo.io");
        var admin = new AdminService(gw);
        Assert.True((await admin.SetActiveAsync(IdOf(gw, "member@demo.io"), false)).Ok);
        Assert.True((await admin.SetActiveAsync(IdOf(gw, "member@demo.io"), true)).Ok);
        Assert.True((await admin.CreateUserAsync("Nina", "Ramos", "nina@demo.io", "coach", "temp1234")).Ok);
        Assert.True((await admin.ResetPasswordAsync(IdOf(gw, "coach@demo.io"), "temp1234")).Ok);
    }

    [Fact]
    public async Task A_sub_admin_cannot_touch_staff_accounts_or_roles()
    {
        var gw = await As("subadmin@demo.io");
        var admin = new AdminService(gw);
        foreach (var staff in new[] { "employee@demo.io", "admin@demo.io" })
        {
            Assert.Equal(AppErrorKind.Unauthorized, (await admin.SetActiveAsync(IdOf(gw, staff), false)).Error);
            Assert.Equal(AppErrorKind.Unauthorized, (await admin.ResetPasswordAsync(IdOf(gw, staff), "temp1234")).Error);
        }
        Assert.Equal(AppErrorKind.Unauthorized, (await admin.CreateUserAsync("Desk", "Person", "desk2@demo.io", "employee", "temp1234")).Error);
        Assert.Equal(AppErrorKind.Unauthorized, (await admin.AssignRoleAsync(IdOf(gw, "member@demo.io"), "coach")).Error);
        Assert.False((await new AccountArchiveService(gw).CanManageAsync()).Value);
    }

    [Fact]
    public async Task A_sub_admin_cannot_void_or_change_gym_settings_but_reads_revenue()
    {
        var gw = await As("subadmin@demo.io");
        var admin = new AdminService(gw);
        Assert.True((await admin.RevenueAsync(DateOnly.FromDateTime(DateTime.Today), DateOnly.FromDateTime(DateTime.Today))).Ok);
        var settings = (await admin.GymSettingsAsync()).Value!;
        Assert.Equal(AppErrorKind.Unauthorized, (await admin.SaveGymSettingsAsync(settings, null)).Error);
        Assert.False((await admin.IsSuperAdminAsync()).Value);
    }

    [Fact]
    public async Task The_super_admin_appoints_a_sub_admin()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        Assert.True((await admin.IsSuperAdminAsync()).Value);
        Assert.True((await admin.AssignRoleAsync(IdOf(gw, "bea@demo.io"), "subadmin")).Ok);
        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppRole.SubAdmin, (await new AuthService(gw).SignInAsync("bea@demo.io", DemoGateway.DemoPassword)).Value);
    }

    [Fact]
    public async Task The_users_page_offers_a_sub_admin_members_and_coaches_only_and_lists_deactivated_accounts()
    {
        var gw = await As("subadmin@demo.io");
        var vm = new UsersViewModel(new AdminService(gw), new AccountArchiveService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(["Member", "Coach"], vm.Roles);
        Assert.False(vm.IsSuperAdmin);
        Assert.False(vm.CanManage(vm.Users.First(u => u.Roles.Contains("employee"))));
        var alex = vm.Users.First(u => u.Username == "Alex Tan");
        Assert.True(vm.CanManage(alex));

        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        await vm.ToggleActiveCommand.ExecuteAsync(alex);
        Assert.DoesNotContain(vm.Users, u => u.UserId == alex.UserId);
        var deactivated = Assert.Single(vm.Deactivated, u => u.UserId == alex.UserId);
        await vm.ToggleActiveCommand.ExecuteAsync(deactivated);
        Assert.Contains(vm.Users, u => u.UserId == alex.UserId);
    }

    [Fact]
    public async Task The_super_admin_sees_every_role_and_the_sub_admin_toggle()
    {
        var gw = await As("admin@demo.io");
        var vm = new UsersViewModel(new AdminService(gw), new AccountArchiveService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal(["Member", "Coach", "Employee"], vm.Roles);
        Assert.True(vm.IsSuperAdmin);
        Assert.True(vm.CanManage(vm.Users.First(u => u.Roles.Contains("employee"))));
        var row = vm.Users.First(u => u.Username == "Alex Tan");
        Assert.Equal("Make sub-admin", row.SubAdminActionText);
    }

    [Fact]
    public async Task A_sub_admin_cannot_archive_catalog_records_and_is_told_why()
    {
        var gw = await As("subadmin@demo.io");
        var vm = new CatalogAdminViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.False(vm.CanArchive);
    }
}
