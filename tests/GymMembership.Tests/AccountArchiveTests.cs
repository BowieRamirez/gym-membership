using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class AccountArchiveTests
{
    static async Task<(DemoGateway Gateway, string Target)> Setup()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AdminService(gw).CreateUserAsync("Archive", "Person", "archive@demo.io", "employee", "secret12")).Ok);
        var target = (await gw.ListAsync<Profile>()).Single(p => p.FullName == "Archive Person").Id;
        return (gw, target);
    }

    static Task Archive(DemoGateway gw, string id, string reason = "Account no longer needed") =>
        gw.RpcAsync("archive_account", new() { ["p_user"] = id, ["p_reason"] = reason });

    [Fact]
    public async Task Archive_is_a_soft_delete_with_an_audit_event()
    {
        var (gw, id) = await Setup();
        await Archive(gw, id);
        Assert.False((await gw.ListAsync<Profile>()).Single(p => p.Id == id).IsActive);
        Assert.Contains(await gw.ListAsync<AuditEntry>(), a => a.Action == "user.archive" && a.EntityId == id && a.ActorId == "u-admin");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Archive_requires_a_nonblank_reason(string reason)
    {
        var (gw, id) = await Setup();
        Assert.Equal("bad_archive_reason", (await Assert.ThrowsAsync<Exception>(() => Archive(gw, id, reason))).Message);
        Assert.True((await gw.ListAsync<Profile>()).Single(p => p.Id == id).IsActive);
    }

    [Fact]
    public async Task Archive_rejects_overlong_reasons_and_self_deletion()
    {
        var (gw, id) = await Setup();
        Assert.Equal("bad_archive_reason", (await Assert.ThrowsAsync<Exception>(() => Archive(gw, id, new string('a', 501)))).Message);
        Assert.Equal("self_archive", (await Assert.ThrowsAsync<Exception>(() => Archive(gw, "u-admin"))).Message);
    }

    [Theory]
    [InlineData("employee@demo.io")]
    [InlineData("coach@demo.io")]
    [InlineData("member@demo.io")]
    public async Task Nonadmins_cannot_archive_accounts(string email)
    {
        var (gw, id) = await Setup();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<Exception>(() => Archive(gw, id))).Message);
    }

    [Fact]
    public async Task Archive_refuses_accounts_with_active_coaching_commitments()
    {
        var (gw, _) = await Setup();
        Assert.Equal("account_has_commitments", (await Assert.ThrowsAsync<Exception>(() => Archive(gw, "u-alex"))).Message);
    }

    [Fact]
    public async Task Archived_accounts_cannot_sign_in_through_the_app_or_write_with_existing_identity()
    {
        var (gw, id) = await Setup();
        await Archive(gw, id);
        var result = await new AuthService(gw).SignInAsync("archive@demo.io", "secret12");
        Assert.Equal(AppErrorKind.Unauthorized, result.Error);
        await gw.SignInAsync("archive@demo.io", "secret12");
        Assert.Empty(await gw.ListAsync<Payment>());
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<Exception>(() => gw.RpcAsync("touch_last_seen"))).Message);
        Assert.Equal("forbidden", (await Assert.ThrowsAsync<Exception>(() => gw.RpcAsync("mark_password_changed"))).Message);
    }

    [Fact]
    public async Task Repeat_archiving_does_not_replace_the_original_reason_or_add_events()
    {
        var (gw, id) = await Setup();
        await Archive(gw, id);
        Assert.Equal("already_processed", (await Assert.ThrowsAsync<Exception>(() => Archive(gw, id, "Replacement reason"))).Message);
        Assert.Single(await gw.ListAsync<AuditEntry>(), a => a.Action == "user.archive" && a.EntityId == id);
    }

    [Fact]
    public async Task Normal_reactivation_cannot_bypass_archive_restore()
    {
        var (gw, id) = await Setup();
        await Archive(gw, id);
        var result = await new AdminService(gw).SetActiveAsync(id, true);
        Assert.Equal("account_archived", result.Message);
        await gw.RpcAsync("restore_account", new() { ["p_user"] = id });
        Assert.True((await gw.ListAsync<Profile>()).Single(p => p.Id == id).IsActive);
        Assert.Contains(await gw.ListAsync<AuditEntry>(), a => a.Action == "user.restore" && a.EntityId == id);
    }
}
