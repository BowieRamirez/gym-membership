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
        if (Has(m, "account_has_commitments", "account_archived", "record_archived", "record_not_restorable", "code_limit_reached", "not_archived", "already_processed", "already_availed", "renewal_pending", "already_requested", "refund_in_progress", "not_refundable", "pending_session_request", "active_coaching_session", "overlap", "in_use", "23505", "23p01", "23503", "p0002", "duplicate key", "foreign key")) return AppErrorKind.Conflict;
        if (Has(m, "account_inactive", "bad_archive_reason", "self_archive", "last_active_admin", "no_active_membership", "self_verify", "self_decision", "not_renewable", "wrong_member", "not_found", "23514",
                   "refund_window_closed", "payment_expired", "invite_member_inactive", "invalid_code", "limit_required", "limit_below_used", "22023", "bad_input", "bad_image", "bad_reason", "bad_back_at", "bad_status", "bad_discount", "email_taken", "no_active_hire", "empty_message", "message_too_long", "wrong_password", "email_confirmation_required", "end must be after start")) return AppErrorKind.Validation;
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

    /// <summary>Prefers a specific sentence for the few failures the user can fix; falls back to the generic text.</summary>
    public static string For(AppErrorKind kind, string? raw) => raw switch
    {
        "bad_archive_reason" => "Enter a reason for deletion, from 1 to 500 characters.",
        "self_archive" => "You can't delete your own account.",
        "last_active_admin" => "Keep at least one active administrator.",
        "account_has_commitments" => "Resolve this account's scheduled sessions, active coaching hires and pending payments before deleting it.",
        "account_archived" => "This account is archived. An administrator must restore it from Archive.",
        "not_archived" => "This record is no longer archived. Refresh Archive and try again.",
        "record_archived" => "This record is archived and read-only. An administrator must restore it before editing.",
        "record_not_restorable" => "This code has expired or reached its limit and cannot be restored. Create a new code instead.",
        "code_limit_reached" => "That discount code has been used up.",
        "limit_required" => "Enter a limit of at least 1 for how many times the code can be used.",
        "limit_below_used" => "The limit can't be lower than the number of times the code is already used or held by a pending payment.",
        "bad_archive_filter" => "Check the archive filters and date range, then try again.",
        "account_inactive" => "This account is inactive and cannot receive a new booking, coaching hire or pending payment.",
        "invalid_code" => "That discount code isn't valid, or it has expired.",
        "bad_input" => "Fill in every field. The password needs at least 6 characters.",
        "bad_name" => "Use letters only in your name. Spaces, hyphens and apostrophes are fine. 2 to 60 characters.",
        "bad_email" => "Enter a valid email address, for example name@example.com. Up to 100 characters.",
        "bad_password" => "Use 6 to 64 characters for your password.",
        "bad_image" => "Choose a JPG, PNG or WebP picture under 5 MB.",
        "message_too_long" => "Keep a message under 2000 characters.",
        "bad_back_at" => "Pick a return time that is still ahead.",
        "bad_reason" => "Keep the reason under 200 characters.",
        "bad_status" => "Choose Available, In use or Unavailable.",
        "bad_discount" => "Enter a code and a percent between 1 and 100.",
        "email_confirmation_required" => "Check your email and confirm your address, then sign in.",
        "wrong_password" => "Your current password isn't right.",
        "email_taken" =>"Someone already uses that email.",
        "in_use" => "Existing records use this, so it can't be deleted. Turn it off instead.",
        "already_availed" => "You already have this, or a payment for it is waiting. Renew it, or wait for the payment to be reviewed.",
        "renewal_pending" => "A membership or extension payment is already waiting. Finish that payment first.",
        "refund_window_closed" => "The 24-hour cancellation and refund request window has closed.",
        "payment_expired" => "That unpaid payment has expired. Create a fresh payment.",
        "already_requested" => "A cancellation and refund request already exists for this term.",
        "active_coaching_session" => "You already have an active session together. Open Sessions to reschedule or cancel it. Finish, cancel or mark that session no-show before requesting another.",
        "pending_session_request" => "You already have a pending request to this coach. Wait for a reply or cancel it below before sending another.",
        "invite_member_inactive" => "This member needs an active membership before you can invite them to a session.",
        _ when raw?.Contains("time_requests_one_pending_member_per_coach", StringComparison.OrdinalIgnoreCase) == true => "You already have a pending request to this coach. Wait for a reply or cancel it below before sending another.",
        "not_refundable" => "This term has no eligible verified payment to refund.",
        "refund_in_progress" => "This payment has a pending or approved refund. It cannot also be voided.",
        "not_renewable" => "This membership can't be renewed right now.",
        "self_decision" => "You can't decide or repay your own refund request.",
        "self_verify" => "You can't approve a payment you made yourself.",
        "no_active_membership" => "You need an active membership for that. Buy a package and wait for the payment to be verified.",
        "already_processed" => "That was already handled. Refresh the page and try again.",
        "no_active_hire" => "You can only message a coach you have hired, or a trainee who is hired to you.",
        "empty_message" => "Type a message first.",
        "end must be after start" => "The end time must be after the start time.",
        // the app is newer than the database: a migration was not pushed, so the server cannot find a function, table or column it asks for
        _ when raw?.Contains("schema cache", StringComparison.OrdinalIgnoreCase) == true => "This part of the app needs a server update that hasn't been installed yet. Tell the gym owner.",
        _ when raw?.Contains("foreign key", StringComparison.OrdinalIgnoreCase) == true => "Existing records use this, so it can't be deleted. Turn it off instead.",
        _ when raw?.Contains("payments_reference_no_key", StringComparison.OrdinalIgnoreCase) == true => "That reference number was already used on another payment.",
        _ when raw?.Contains("23P01", StringComparison.OrdinalIgnoreCase) == true || raw?.Contains("conflicting key", StringComparison.OrdinalIgnoreCase) == true => "That time overlaps something already booked.",
        _ => For(kind)
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
    /// <summary>Opens the area for a role. changePassword sends them to Settings first, because they are still on a temporary password.</summary>
    void GoTo(AppRole role, bool changePassword = false);
    void GoToLogin();
    void GoToRegister();
    void GoToMessages();
    void GoToMembership();
    void ApplyTheme(string theme);
}

public static class StoragePath
{
    /// <summary>The folder stays; the file name keeps letters, digits, dot, dash and underscore (a camera file can hold spaces and accents the bucket refuses).</summary>
    public static string Safe(string path)
    {
        var slash = path.LastIndexOf('/');
        return path[..(slash + 1)] + System.Text.RegularExpressions.Regex.Replace(path[(slash + 1)..], @"[^A-Za-z0-9._-]", "_");
    }
}

public interface IProofPicker { Task<(string FileName, byte[] Data)?> PickAsync(); }

/// <summary>The only seam to the backend. DemoGateway implements it in memory; SupabaseGateway talks to the real project.</summary>
public interface ISupabaseGateway
{
    string? CurrentUserId { get; }
    Task SignInAsync(string email, string password);
    Task SignUpAsync(string email, string password, string fullName);
    /// <summary>Checks the current password, then sets the new one. The caller then runs the mark_password_changed function.</summary>
    Task ChangePasswordAsync(string currentPassword, string newPassword);
    Task SignOutAsync();
    Task<bool> RestoreSessionAsync();

    Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new();
    Task<T> InsertAsync<T>(T row) where T : BaseModel, new();
    Task UpdateAsync<T>(T row) where T : BaseModel, new();
    Task DeleteAsync<T>(T row) where T : BaseModel, new();
    /// <summary>Sets one column on every row that matches, in a single request; rows the filter does not match are left alone.</summary>
    Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new();
    Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null);
    Task RpcAsync(string function, Dictionary<string, object>? args = null);
    Task<string> UploadAsync(string bucket, string path, byte[] data);
    /// <summary>A short-lived link to a private file (a payment screenshot, the gym's QR), or null if it does not exist.</summary>
    Task<string?> SignedUrlAsync(string bucket, string path);
    /// <summary>Calls an Edge Function (create-user, reset-password), which holds the service-role key the app never has.</summary>
    Task InvokeFunctionAsync(string name, Dictionary<string, object> body);
}
