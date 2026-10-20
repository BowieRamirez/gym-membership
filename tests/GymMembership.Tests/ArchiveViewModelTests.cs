using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class ArchiveViewModelTests
{
    static async Task<(DemoGateway Gateway, ArchiveViewModel Vm)> Setup(string email = "admin@demo.io")
    {
        var gw = new DemoGateway(); await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new ArchiveViewModel(new ArchiveService(gw), new AccountArchiveService(gw));
        await vm.OnAppearingAsync(); return (gw, vm);
    }

    [Fact]
    public async Task Filters_combine_clear_in_one_action_and_empty_is_not_an_error()
    {
        var (_, vm) = await Setup();
        Assert.True(vm.CanManage); Assert.NotEmpty(vm.Rows);
        vm.TypeIndex = 2; vm.StatusIndex = 3; vm.UseDates = true;
        vm.From = DateTime.Today.AddYears(-2); vm.To = DateTime.Today.AddYears(1); vm.Reason = "not present";
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows); Assert.True(vm.IsEmpty); Assert.Null(vm.ErrorMessage);
        await vm.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.TypeIndex); Assert.Equal(0, vm.StatusIndex); Assert.False(vm.UseDates); Assert.Empty(vm.Reason);
        Assert.NotEmpty(vm.Rows); Assert.False(vm.IsEmpty); Assert.Equal(0, vm.Page);
    }

    [Fact]
    public async Task Invalid_date_range_does_not_replace_results()
    {
        var (_, vm) = await Setup(); var before = vm.Rows.ToArray();
        vm.UseDates = true; vm.From = DateTime.Today; vm.To = DateTime.Today.AddDays(-1);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Equal(before, vm.Rows.ToArray());
    }

    [Fact]
    public async Task Restore_requires_explicit_confirmation_and_refreshes_results()
    {
        var (gw, vm) = await Setup(); var admin = new AdminService(gw);
        var plan = (await gw.ListAsync<MembershipPackage>()).First(); await admin.DeletePackageAsync(plan);
        await vm.OnAppearingAsync(); var row = vm.Rows.Single(r => r.Type == "plan" && r.RecordId == plan.Id.ToString());
        vm.ViewCommand.Execute(row);
        await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.False((await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == plan.Id).IsActive);
        vm.RequestRestoreCommand.Execute(null); vm.CancelRestoreCommand.Execute(null);
        Assert.False(vm.ConfirmingRestore); Assert.Same(row, vm.Selected);
        vm.RequestRestoreCommand.Execute(null); await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.True((await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == plan.Id).IsActive);
        Assert.Null(vm.Selected); Assert.Contains("restored", vm.Notice);
        Assert.DoesNotContain(vm.Rows, r => r.Type == "plan" && r.RecordId == row.RecordId);
    }

    [Fact]
    public async Task Expired_code_has_no_restore_action()
    {
        var (_, vm) = await Setup(); var expired = vm.Rows.First(r => r.Status == "expired");
        vm.ViewCommand.Execute(expired); vm.RequestRestoreCommand.Execute(null);
        Assert.False(vm.ConfirmingRestore);
    }

    [Fact]
    public async Task Permission_loss_clears_previously_visible_records()
    {
        var (gw, vm) = await Setup(); Assert.NotEmpty(vm.Rows);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await vm.OnAppearingAsync(); Assert.False(vm.CanManage); Assert.Empty(vm.Rows); Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Server_error_is_not_shown_as_empty_and_failed_restore_keeps_details()
    {
        var (gw, vm) = await Setup(); var plan = (await gw.ListAsync<MembershipPackage>()).First();
        await new AdminService(gw).DeletePackageAsync(plan); await vm.OnAppearingAsync();
        var row = vm.Rows.First(r => r.Type == "plan"); vm.ViewCommand.Execute(row); vm.RequestRestoreCommand.Execute(null);
        gw.FailWith = new Exception("offline");
        await vm.ConfirmRestoreCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage); Assert.Same(row, vm.Selected); Assert.True(vm.ConfirmingRestore);
        await vm.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Empty(vm.Rows); Assert.False(vm.IsEmpty); Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Pagination_keeps_applied_filters_until_apply_is_clicked()
    {
        var (gw, vm) = await Setup(); var admin = new AdminService(gw);
        for (var i = 0; i < 33; i++)
        {
            var plan = new MembershipPackage { Name = $"Archived {i}", Price = 1, DurationDays = 1 };
            await admin.SavePackageAsync(plan); await admin.DeletePackageAsync(plan);
        }
        vm.TypeIndex = 3; await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(30, vm.Rows.Count); Assert.True(vm.HasMore);
        var ids = vm.Rows.Select(r => r.RecordId).ToArray(); vm.TypeIndex = 2;
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.Page); Assert.Equal(3, vm.Rows.Count); Assert.All(vm.Rows, r => Assert.Equal("plan", r.Type));
        Assert.DoesNotContain(vm.Rows, r => ids.Contains(r.RecordId)); Assert.False(vm.HasMore);
        await vm.PreviousCommand.ExecuteAsync(null); Assert.Equal(ids, vm.Rows.Select(r => r.RecordId));
    }
}
