using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

// Phase 3 of the 2026-11-12 plan (G6, G7, G9; 0061) in demo mode: drafts, publishing, versions, delete versus archive, and renewals
// into the newest version.
public class CatalogLifecycleTests
{
    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    static async Task<MembershipPackage> Package(DemoGateway gw, string name) => (await gw.ListAsync<MembershipPackage>()).Single(p => p.Name == name && p.IsActive);

    static async Task<MembershipPackage> AddDraft(AdminService admin, DemoGateway gw, string name, string? description = "Six weeks")
    {
        Assert.True((await admin.SavePackageAsync(new MembershipPackage { Name = name, Description = description, Price = 2000, DurationDays = 42, Benefits = ["Gym floor"] })).Ok);
        return (await gw.ListAsync<MembershipPackage>()).Single(p => p.Name == name);
    }

    [Fact]
    public void Changes_between_two_versions_name_only_what_changed()
    {
        var old = new MembershipPackage { Name = "Monthly", Description = "30 days", Price = 1500, DurationDays = 30, Benefits = ["Gym floor"] };
        var next = new MembershipPackage { Name = "Monthly", Description = "30 days", Price = 1800, DurationDays = 30, Benefits = ["Gym floor", "Lockers"] };
        Assert.Equal([("Price", "₱1,500.00", "₱1,800.00"), ("Benefits", "Gym floor", "Gym floor; Lockers")],
            PackageChanges.Between(old, next).Select(c => (c.Field, c.Old, c.New)));
        Assert.Empty(PackageChanges.Between(old, old));
    }

    [Fact]
    public async Task A_new_package_is_a_draft_members_never_see_or_buy()
    {
        var gw = await As("admin@demo.io");
        var draft = await AddDraft(new AdminService(gw), gw, "Six weeks");
        Assert.Equal(CatalogState.Draft, draft.State);

        await As("carlo@demo.io", gw);
        Assert.DoesNotContain((await new MembershipService(gw).ListPackagesAsync()).Value!, p => p.Id == draft.Id);
        var buy = await new MembershipService(gw).AvailAsync(draft.Id);
        Assert.Equal("item_not_for_sale", buy.Message);
    }

    [Fact]
    public async Task Publishing_needs_every_required_field_and_then_locks_the_package()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var draft = await AddDraft(admin, gw, "Six weeks", description: " ");
        Assert.Equal("missing_fields", (await admin.PublishAsync("plan", draft.Id)).Message);

        draft.Description = "Six weeks of full access.";
        Assert.True((await admin.SavePackageAsync(draft)).Ok);   // a draft can be edited
        Assert.True((await admin.PublishAsync("plan", draft.Id)).Ok);
        var published = await Package(gw, "Six weeks");
        Assert.Equal(CatalogState.Published, published.State);

        published.Price = 1;
        Assert.Equal("package_published", (await admin.SavePackageAsync(published)).Message);
    }

    [Fact]
    public async Task A_new_version_replaces_the_old_one_when_published_and_renewals_move_to_it()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var monthly = await Package(gw, "Monthly");
        var versionId = (await admin.NewPackageVersionAsync(monthly.Id)).Value;
        Assert.Equal("draft_exists", (await admin.NewPackageVersionAsync(monthly.Id)).Message);

        var version = (await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == versionId);
        Assert.Equal((monthly.Name, monthly.Description, monthly.Price, monthly.DurationDays, (int?)monthly.Id, CatalogState.Draft),
            (version.Name, version.Description, version.Price, version.DurationDays, version.ReplacesId, version.State));
        version.Price = 1800;
        Assert.True((await admin.SavePackageAsync(version)).Ok);
        Assert.Equal(CatalogState.Published, (await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == monthly.Id).State);   // still on sale meanwhile

        Assert.True((await admin.PublishAsync("plan", versionId)).Ok);
        Assert.Equal(CatalogState.Archived, (await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == monthly.Id).State);
        Assert.Equal("package_archived", (await new ArchiveService(gw).RestoreAsync(new() { Type = "plan", RecordId = monthly.Id.ToString() })).Message);

        await As("member@demo.io", gw);   // Alex is on the old Monthly
        var alex = (await gw.ListAsync<UserMembershipPackage>()).Single(m => m.MemberId == "u-alex");
        var renewal = await new MembershipService(gw).RenewAsync(alex.Id);
        Assert.Equal(1800, renewal.Value!.Amount);
    }

    [Fact]
    public async Task A_draft_is_deleted_and_anything_published_is_archived_instead()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var draft = await AddDraft(admin, gw, "Six weeks");
        Assert.True((await admin.DeleteDraftAsync("plan", draft.Id)).Ok);
        Assert.DoesNotContain(await gw.ListAsync<MembershipPackage>(), p => p.Id == draft.Id);
        Assert.Equal("published_archive_instead", (await admin.DeleteDraftAsync("plan", (await Package(gw, "Annual")).Id)).Message);
    }

    [Fact]
    public void Every_new_refusal_has_its_own_sentence()
    {
        foreach (var code in new[] { "item_not_for_sale", "missing_fields", "already_published", "package_published", "package_archived", "name_taken", "draft_exists", "not_published", "published_archive_instead" })
            Assert.NotEqual(ErrorText.For(AppErrorKind.Conflict), ErrorText.For(AppErrorKind.Conflict, code));
    }
}
