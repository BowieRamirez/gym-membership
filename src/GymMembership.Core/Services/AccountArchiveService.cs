using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public interface IAccountArchiveService
{
    Task<AppResult<bool>> CanManageAsync();
    Task<AppResult<bool>> DeleteAsync(string userId, string reason);
    Task<AppResult<bool>> RestoreAsync(string userId);
}

public sealed class AccountArchiveService(ISupabaseGateway gw) : IAccountArchiveService
{
    public Task<AppResult<bool>> CanManageAsync() => Safe.RunAsync(async () =>
    {
        if (gw.CurrentUserId is not { } uid) return false;
        var profileTask = gw.ListAsync<Profile>(p => p.Id == uid);
        var rolesTask = gw.ListAsync<Role>();
        var linksTask = gw.ListAsync<UserRole>(r => r.UserId == uid);
        await Task.WhenAll(profileTask, rolesTask, linksTask);
        if (profileTask.Result.FirstOrDefault()?.IsActive != true) return false;
        var roles = rolesTask.Result;
        return linksTask.Result.Any(l => roles.Any(r => r.RoleId == l.RoleId && r.Name == "admin"));
    });

    public Task<AppResult<bool>> DeleteAsync(string userId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            return Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Validation, "bad_archive_reason"));
        return Safe.RunAsync(async () =>
        {
            await gw.RpcAsync("archive_account", new() { ["p_user"] = userId, ["p_reason"] = reason.Trim() });
            return true;
        });
    }

    public Task<AppResult<bool>> RestoreAsync(string userId) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("restore_account", new() { ["p_user"] = userId });
        return true;
    });
}
