using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AccountDeletionViewModelTests
{
    static UserRow Target => new("target", "Archive Person", true, ["member"]);

    sealed class ArchiveStub : IAccountArchiveService
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<AppResult<bool>>? Pending { get; set; }
        public AppResult<bool> Result { get; set; } = AppResult<bool>.Success(true);
        public Task<AppResult<bool>> CanManageAsync() => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> DeleteAsync(string id, string reason) { Calls++; return Pending?.Task ?? Task.FromResult(Result); }
        public Task<AppResult<bool>> RestoreAsync(string id) => Task.FromResult(Result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task Empty_reason_cannot_confirm_or_mutate(string reason)
    {
        var service = new ArchiveStub();
        var vm = new AccountDeletionViewModel(service, Target) { Reason = reason };
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(0, service.Calls);
        Assert.False(vm.Deleted);
    }

    [Fact]
    public void Reason_changes_refresh_confirmation_and_enforce_the_maximum()
    {
        var vm = new AccountDeletionViewModel(new ArchiveStub(), Target);
        var changes = 0; vm.ConfirmCommand.CanExecuteChanged += (_, _) => changes++;
        vm.Reason = new string('a', 500);
        Assert.True(vm.ConfirmCommand.CanExecute(null));
        vm.Reason += "a";
        Assert.False(vm.ConfirmCommand.CanExecute(null));
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Saving_blocks_duplicate_submission_and_only_success_finishes()
    {
        var service = new ArchiveStub { Pending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new AccountDeletionViewModel(service, Target) { Reason = "Duplicate account" };
        var operation = vm.ConfirmCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy); Assert.False(vm.ConfirmCommand.CanExecute(null));
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(1, service.Calls);
        service.Pending.SetResult(AppResult<bool>.Success(true));
        await operation;
        Assert.True(vm.Deleted); Assert.False(vm.IsBusy); Assert.False(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Server_failure_keeps_the_dialog_target_and_reason_for_retry()
    {
        var service = new ArchiveStub { Result = AppResult<bool>.Fail(AppErrorKind.Conflict, "account_has_commitments") };
        var vm = new AccountDeletionViewModel(service, Target) { Reason = "Duplicate account" };
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.False(vm.Deleted); Assert.Equal(Target.UserId, vm.User.UserId); Assert.Equal("Duplicate account", vm.Reason);
        Assert.Contains("pending payments", vm.ErrorMessage); Assert.True(vm.ConfirmCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("admin@demo.io", true)]
    [InlineData("employee@demo.io", false)]
    [InlineData("coach@demo.io", false)]
    [InlineData("member@demo.io", false)]
    public async Task Only_admin_can_open_the_delete_flow(string email, bool allowed)
    {
        var gw = new DemoGateway(); await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new UsersViewModel(new AdminService(gw), new AccountArchiveService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal(allowed, vm.CanDeleteAccounts);
        Assert.Equal(allowed, vm.DeletionFor(Target) is not null);
    }

    [Fact]
    public async Task Service_trims_and_persists_reason_and_active_list_hides_deleted_accounts()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        await admin.CreateUserAsync("Archive", "Person", "archive@demo.io", "employee", "secret12");
        var id = (await gw.ListAsync<Profile>()).Single(p => p.FullName == "Archive Person").Id;
        Assert.True((await new AccountArchiveService(gw).DeleteAsync(id, "  Duplicate account  ")).Ok);
        var archived = (await gw.ListAsync<Profile>()).Single(p => p.Id == id);
        Assert.Equal("Duplicate account", archived.DeletionReason); Assert.Equal("u-admin", archived.DeletedBy); Assert.NotNull(archived.DeletedAt);
        Assert.DoesNotContain((await admin.UsersAsync()).Value!, u => u.UserId == id);
        Assert.True((await new AccountArchiveService(gw).RestoreAsync(id)).Ok);
        Assert.Contains((await admin.UsersAsync()).Value!, u => u.UserId == id);
    }

    [Fact]
    public void UI_uses_one_overflow_button_and_required_reason_popup_without_inline_actions()
    {
        var users = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Views", "UsersPage.xaml"));
        Assert.Contains("OnAccountActions", users); Assert.DoesNotContain("OnToggleRoleCoach", users);
        Assert.DoesNotContain("OnToggleRoleEmployee", users); Assert.DoesNotContain("OnToggleActive", users);
        var popup = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "Controls", "DeleteAccountPopup.xaml"));
        Assert.Contains("Reason for deletion", popup); Assert.Contains("<Editor", popup);
        Assert.Contains("No, Cancel", popup); Assert.Contains("Text=\"Confirm\"", popup); Assert.Contains("PlateRedLight", popup);
    }
}
