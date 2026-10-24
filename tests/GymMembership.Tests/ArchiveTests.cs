using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class ArchiveTests
{
    static async Task<DemoGateway> Admin()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword); return gw;
    }

    [Fact]
    public async Task Catalog_deletion_archives_used_plans_instead_of_removing_history()
    {
        var gw = await Admin(); var admin = new AdminService(gw); var archive = new ArchiveService(gw);
        var plan = (await gw.ListAsync<MembershipPackage>()).First(p => p.Name == "Monthly");
        var history = (await gw.ListAsync<UserMembershipPackage>()).Count;
        Assert.True((await admin.DeletePackageAsync(plan)).Ok);
        Assert.False((await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == plan.Id).IsActive);
        Assert.Equal(history, (await gw.ListAsync<UserMembershipPackage>()).Count);
        var row = Assert.Single((await archive.ListAsync(new(Type: "plan"), 0)).Value!.Rows, r => r.RecordId == plan.Id.ToString());
        Assert.NotNull(row.ArchivedAt); Assert.Contains("plan", row.Reason);
        Assert.True((await archive.RestoreAsync(row)).Ok);
        Assert.True((await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == plan.Id).IsActive);
    }

    [Fact]
    public async Task All_four_types_share_the_archive_with_combined_filters()
    {
        var gw = await Admin(); var admin = new AdminService(gw); var service = new ArchiveService(gw);
        await admin.CreateUserAsync("Archive", "Person", "archived@demo.io", "employee", "secret12");
        var person = (await gw.ListAsync<Profile>()).Single(p => p.FullName == "Archive Person");
        Assert.True((await new AccountArchiveService(gw).DeleteAsync(person.Id, "Duplicate account")).Ok);
        Assert.True((await admin.DeletePackageAsync((await gw.ListAsync<MembershipPackage>()).First())).Ok);
        var amenity = (await gw.ListAsync<Amenity>()).First(a => a.IsActive); amenity.IsActive = false;
        Assert.True((await admin.SaveAmenityAsync(amenity)).Ok);
        var rows = (await service.ListAsync(new(), 0)).Value!.Rows;
        Assert.All(new[] { "account", "code", "plan", "amenity" }, type => Assert.Contains(rows, r => r.Type == type));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var filtered = (await service.ListAsync(new("account", "deleted", today, today, "Duplicate"), 0)).Value!;
        Assert.Equal(person.Id, Assert.Single(filtered.Rows).RecordId);
        Assert.Empty((await service.ListAsync(new("account", "deleted", today, today, "not present"), 0)).Value!.Rows);
    }

    [Theory]
    [InlineData("employee@demo.io")]
    [InlineData("coach@demo.io")]
    [InlineData("member@demo.io")]
    public async Task Nonadmins_cannot_read_or_restore_archive(string email)
    {
        var gw = await Admin(); await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var service = new ArchiveService(gw);
        Assert.Equal(AppErrorKind.Unauthorized, (await service.ListAsync(new(), 0)).Error);
        Assert.Equal(AppErrorKind.Unauthorized, (await service.RestoreAsync(new() { Type = "plan", RecordId = "1" })).Error);
    }

    [Fact]
    public async Task Expired_codes_are_archived_and_cannot_be_restored_or_edited()
    {
        var gw = await Admin(); var service = new ArchiveService(gw); var admin = new AdminService(gw);
        var row = Assert.Single((await service.ListAsync(new("code", "expired"), 0)).Value!.Rows);
        Assert.False(row.CanRestore); Assert.Equal("record_not_restorable", (await service.RestoreAsync(row)).Message);
        var code = (await gw.ListAsync<Discount>()).Single(d => d.Id.ToString() == row.RecordId);
        code.ExpiresAt = DateTime.UtcNow.AddYears(1);
        Assert.Equal("record_archived", (await admin.SaveDiscountAsync(code)).Message);
    }

    [Fact]
    public async Task Archived_catalog_is_read_only_even_with_a_stale_active_payload()
    {
        var gw = await Admin(); var admin = new AdminService(gw);
        var plan = (await gw.ListAsync<MembershipPackage>()).First();
        await admin.DeletePackageAsync(plan);
        plan.IsActive = true; plan.Name = "Stale change";
        Assert.Equal("record_archived", (await admin.SavePackageAsync(plan)).Message);
        var amenity = (await gw.ListAsync<Amenity>()).First(); amenity.IsActive = false;
        await admin.SaveAmenityAsync(amenity);
        amenity.Name = "Edited in archive";
        Assert.Equal("record_archived", (await admin.SaveAmenityAsync(amenity)).Message);
        await Assert.ThrowsAsync<Exception>(() => gw.DeleteAsync(plan));
    }

    [Fact]
    public async Task Inactive_accounts_restore_only_through_admin_archive_actions()
    {
        var gw = await Admin(); var admin = new AdminService(gw);
        await admin.SetActiveAsync("u-carlo", false);
        var service = new ArchiveService(gw);
        var row = Assert.Single((await service.ListAsync(new("account", "inactive"), 0)).Value!.Rows, r => r.RecordId == "u-carlo");
        Assert.NotNull(row.ArchivedAt); Assert.True((await service.RestoreAsync(row)).Ok);
        Assert.DoesNotContain((await service.ListAsync(new("account"), 0)).Value!.Rows, r => r.RecordId == "u-carlo");
    }
}
