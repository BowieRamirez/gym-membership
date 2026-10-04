using System.Linq.Expressions;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Services;

public enum AppErrorKind { None, Offline, Unauthorized, Conflict, Validation, Unknown }

public sealed record AppResult<T>(T? Value, AppErrorKind Error = AppErrorKind.None, string? Message = null)
{
    public bool Ok => Error == AppErrorKind.None;
    public static AppResult<T> Success(T value) => new(value);
    public static AppResult<T> Fail(AppErrorKind kind, string? message = null) => new(default, kind, message);
}

public static class Safe
{
    public static async Task<AppResult<T>> RunAsync<T>(Func<Task<T>> work)
    {
        try { return AppResult<T>.Success(await work()); }
        catch (Exception ex) { return AppResult<T>.Fail(ErrorMapper.Map(ex), ex.Message); }
    }
}

public static class ErrorMapper
{
    public static AppErrorKind Map(Exception ex) => ex switch
    {
        HttpRequestException or TaskCanceledException or TimeoutException => AppErrorKind.Offline,
        _ => FromMessage(ex.Message)
    };

    static AppErrorKind FromMessage(string message)
    {
        var m = message.ToLowerInvariant();
        if (Has(m, "forbidden", "42501", "jwt", "row-level security", "invalid login", "not authenticated")) return AppErrorKind.Unauthorized;
        if (Has(m, "already_processed", "already_availed", "renewal_pending", "overlap", "23505", "23p01", "duplicate key")) return AppErrorKind.Conflict;
        if (Has(m, "no_active_membership", "self_verify", "not_renewable", "wrong_member", "not_found", "23514")) return AppErrorKind.Validation;
        return AppErrorKind.Unknown;
    }

    static bool Has(string m, params string[] tokens) => tokens.Any(m.Contains);
}

// Plain words: what happened and what to do next.
public static class ErrorText
{
    public static string For(AppErrorKind kind) => kind switch
    {
        AppErrorKind.Offline => "Can't reach the server. Check your connection and try again.",
        AppErrorKind.Unauthorized => "You don't have permission to do that, or your session ended. Sign in again.",
        AppErrorKind.Conflict => "That clashes with an existing record. Refresh the list and try again.",
        AppErrorKind.Validation => "That isn't allowed right now. For example, you may need an active membership.",
        _ => "Something went wrong. Try again."
    };
}

public enum AppRole { Member, Coach, Employee, Admin }

public static class RoleResolver
{
    public static AppRole Pick(IEnumerable<string> names)
    {
        var set = names.ToHashSet();
        if (set.Contains("admin")) return AppRole.Admin;
        if (set.Contains("employee")) return AppRole.Employee;
        if (set.Contains("coach")) return AppRole.Coach;
        return AppRole.Member;
    }
}

public interface IAppNavigator
{
    void GoTo(AppRole role);
    void GoToLogin();
    void ApplyTheme(string theme);
}

public interface IProofPicker { Task<(string FileName, byte[] Data)?> PickAsync(); }

/// <summary>The only seam to the backend. DemoGateway implements it in memory; a Supabase-backed one comes later.</summary>
public interface ISupabaseGateway
{
    string? CurrentUserId { get; }
    Task SignInAsync(string email, string password);
    Task SignUpAsync(string email, string password, string username);
    Task SignOutAsync();
    Task<bool> RestoreSessionAsync();

    Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new();
    Task<T> InsertAsync<T>(T row) where T : BaseModel, new();
    Task UpdateAsync<T>(T row) where T : BaseModel, new();
    Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null);
    Task RpcAsync(string function, Dictionary<string, object>? args = null);
    Task<string> UploadAsync(string bucket, string path, byte[] data);
}
