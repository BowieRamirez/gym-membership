using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using GymMembership.Core.Services;
using Supabase.Gotrue;
using Supabase.Gotrue.Exceptions;
using Supabase.Postgrest.Models;
using Supabase.Storage;
using Client = Supabase.Client;
using FileOptions = Supabase.Storage.FileOptions;

namespace GymMembership.Core.Backend;

/// <summary>Where the app finds its Supabase project. The anon key is public by design; the service-role key must never be here.</summary>
public sealed record SupabaseSettings(string Url, string AnonKey)
{
    /// <summary>Reads {"url": "...", "anonKey": "..."}. Returns null for a missing, blank or placeholder file, so the app falls back to the demo.</summary>
    public static SupabaseSettings? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString()?.Trim().TrimEnd('/') : null;
            var key = doc.RootElement.TryGetProperty("anonKey", out var k) ? k.GetString()?.Trim() : null;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && !string.IsNullOrEmpty(key) && !key.StartsWith("PASTE", StringComparison.OrdinalIgnoreCase)
                ? new SupabaseSettings(url!, key) : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Keeps the signed-in session between app launches (the MAUI app backs this with SecureStorage).</summary>
public interface ISessionStore
{
    string? Load();
    void Save(string json);
    void Clear();
}

/// <summary>
/// The real backend: talks to a Supabase project. Every rule lives in the database (row level security and the functions in
/// supabase/migrations), so this class only translates calls, sessions and errors. It never holds the service-role key.
/// </summary>
public sealed class SupabaseGateway : ISupabaseGateway
{
    readonly Client _client;
    readonly SemaphoreSlim _startup = new(1, 1);
    bool _started;

    public SupabaseGateway(SupabaseSettings settings, ISessionStore? store = null)
    {
        _client = new Client(settings.Url, settings.AnonKey, new Supabase.SupabaseOptions
        {
            AutoRefreshToken = true,
            AutoConnectRealtime = false,
            SessionHandler = new StoreSessionHandler(store)
        });
    }

    public string? CurrentUserId => _client.Auth.CurrentUser?.Id;

    // for SupabaseRealtimeChat, which lives in this assembly: the connected client and the current sign-in token
    internal async Task<Client> ClientAsync() => await ReadyAsync();
    internal string? AccessToken => _client.Auth.CurrentSession?.AccessToken;

    async Task<Client> ReadyAsync()
    {
        if (_started) return _client;
        await _startup.WaitAsync();
        try { if (!_started) { await _client.InitializeAsync(); _started = true; } }
        finally { _startup.Release(); }
        return _client;
    }

    // ---------- auth ----------
    public async Task SignInAsync(string email, string password)
    {
        var c = await ReadyAsync();
        await c.Auth.SignIn(email.Trim(), password);
    }

    public async Task SignUpAsync(string email, string password, string firstName, string lastName)
    {
        var c = await ReadyAsync();
        Session? session;
        try
        {
            session = await c.Auth.SignUp(email.Trim(), password, new SignUpOptions
            {
                // the sign-up trigger (0004, 0035) reads these; the username is the email so it is always unique
                Data = new Dictionary<string, object>
                {
                    ["first_name"] = firstName, ["last_name"] = lastName, ["full_name"] = NameParts.Join(firstName, lastName),
                    ["username"] = email.Trim().ToLowerInvariant()
                }
            });
        }
        catch (GotrueException ex) when (Regex.IsMatch(ex.Message, "already (been )?registered|already exists", RegexOptions.IgnoreCase))
        {
            throw new Exception("email_taken", ex);
        }
        if (session?.User is null) throw new Exception("email_confirmation_required");   // the project asks new members to confirm their email first
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var c = await ReadyAsync();
        var email = c.Auth.CurrentUser?.Email ?? throw new Exception("not authenticated");
        try { await c.Auth.SignIn(email, currentPassword); }   // Supabase does not ask for the old password, so check it here
        catch (GotrueException) { throw new Exception("wrong_password"); }
        await c.Auth.Update(new UserAttributes { Password = newPassword });
    }

    public string? CurrentEmail => _client.Auth.CurrentUser?.Email;

    // a new address waiting for the code Supabase sent with its email-change mail (projects that confirm email changes)
    string? _pendingEmailChange;

    public async Task SendEmailCodeAsync(string email)
    {
        var c = await ReadyAsync();
        email = email.Trim().ToLowerInvariant();
        await Codes(async () =>
        {
            _pendingEmailChange = null;
            if (!string.Equals(email, c.Auth.CurrentUser?.Email, StringComparison.OrdinalIgnoreCase))
            {
                // with email confirmation off, Supabase switches the address at once; otherwise it emails a change code and waits
                var user = await c.Auth.Update(new UserAttributes { Email = email });
                if (!string.Equals(user?.Email, email, StringComparison.OrdinalIgnoreCase)) { _pendingEmailChange = email; return; }
            }
            await c.Auth.SignInWithOtp(new SignInWithPasswordlessEmailOptions(email) { ShouldCreateUser = false });
        });
    }

    public async Task VerifyEmailCodeAsync(string email, string code)
    {
        var c = await ReadyAsync();
        email = email.Trim().ToLowerInvariant();
        var type = email == _pendingEmailChange ? Constants.EmailOtpType.EmailChange : Constants.EmailOtpType.MagicLink;
        await Codes(() => c.Auth.VerifyOTP(email, code, type));
        _pendingEmailChange = null;
    }

    public async Task SendPasswordResetCodeAsync(string email)
    {
        var c = await ReadyAsync();
        await Codes(() => c.Auth.ResetPasswordForEmail(email.Trim().ToLowerInvariant()));
    }

    public async Task ResetPasswordWithCodeAsync(string email, string code, string newPassword)
    {
        var c = await ReadyAsync();
        await Codes(() => c.Auth.VerifyOTP(email.Trim().ToLowerInvariant(), code, Constants.EmailOtpType.Recovery));
        await Codes(() => c.Auth.Update(new UserAttributes { Password = newPassword }));
    }

    // Supabase's code errors, in the words ErrorText knows: a wrong, used or expired code; asking again too soon; an address in use
    static async Task Codes(Func<Task> call)
    {
        try { await call(); }
        catch (GotrueException ex) when (Regex.IsMatch(ex.Message, "otp_expired|expired or is invalid|invalid.*(otp|token)", RegexOptions.IgnoreCase))
        { throw new Exception("bad_code", ex); }
        catch (GotrueException ex) when (Regex.IsMatch(ex.Message, "rate_limit|only request this after|too many", RegexOptions.IgnoreCase))
        { throw new Exception("resend_wait", ex); }
        catch (GotrueException ex) when (Regex.IsMatch(ex.Message, "email_exists|already (been )?registered|already exists", RegexOptions.IgnoreCase))
        { throw new Exception("email_taken", ex); }
        catch (GotrueException ex) when (Regex.IsMatch(ex.Message, "same_password|different from the old", RegexOptions.IgnoreCase))
        { throw new Exception("same_password", ex); }
    }

    public async Task SignOutAsync()
    {
        var c = await ReadyAsync();
        await c.Auth.SignOut();
    }

    public async Task<bool> RestoreSessionAsync()
    {
        var c = await ReadyAsync();
        var session = await c.Auth.RetrieveSessionAsync();
        return session?.User is not null;
    }

    // ---------- tables ----------
    public async Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new()
    {
        var c = await ReadyAsync();
        var table = c.From<T>();
        var response = where is null ? await table.Get() : await table.Where(where).Get();
        return response.Models;
    }

    public async Task<T> InsertAsync<T>(T row) where T : BaseModel, new()
    {
        var c = await ReadyAsync();
        var response = await c.From<T>().Insert(row);
        return response.Models.FirstOrDefault() ?? row;
    }

    public async Task UpdateAsync<T>(T row) where T : BaseModel, new()
    {
        var c = await ReadyAsync();
        await c.From<T>().Update(row);
    }

    public async Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new()
    {
        var c = await ReadyAsync();
        // the library wants the column as Func<T, object>; boxing the member access keeps the column name it reads from the expression
        var boxed = Expression.Lambda<Func<T, object>>(Expression.Convert(column.Body, typeof(object)), column.Parameters);
        await c.From<T>().Where(where).Set(boxed, value).Update();
    }

    public async Task DeleteAsync<T>(T row) where T : BaseModel, new()
    {
        var c = await ReadyAsync();
        await c.From<T>().Delete(row);
    }

    // ---------- server functions ----------
    public async Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null)
    {
        var c = await ReadyAsync();
        return (await c.Rpc<TResult>(function, args))!;
    }

    public async Task RpcAsync(string function, Dictionary<string, object>? args = null)
    {
        var c = await ReadyAsync();
        await c.Rpc(function, args);
    }

    public async Task InvokeFunctionAsync(string name, Dictionary<string, object> body)
    {
        var c = await ReadyAsync();
        // the signed-in admin's own token goes along, and the function checks it is an admin (supabase/functions/_shared/admin.ts)
        try { await c.Functions.Invoke(name, c.Auth.CurrentSession?.AccessToken, new Supabase.Functions.Client.InvokeFunctionOptions { Body = body }); }
        catch (Exception ex) when (ErrorCode(ex.Message) is { } code) { throw new Exception(code, ex); }   // {"error":"email_taken"} -> email_taken
    }

    // the functions answer a refusal with {"error": "<code>"}; the app's messages are keyed by that code
    static string? ErrorCode(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString() : null; }
        catch (JsonException) { return null; }
    }

    // ---------- files ----------
    static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    { [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp" };

    /// <summary>Uploads an image and returns the path inside the bucket (what the database stores), not "bucket/path".</summary>
    public async Task<string> UploadAsync(string bucket, string path, byte[] data)
    {
        var c = await ReadyAsync();
        var safe = StoragePath.Safe(path);   // spaces and accents in a camera file name
        var mime = MimeByExtension.GetValueOrDefault(Path.GetExtension(safe), "image/jpeg");
        await c.Storage.From(bucket).Upload(data, safe, new FileOptions { ContentType = mime, Upsert = false });
        return safe;
    }

    public async Task<string?> SignedUrlAsync(string bucket, string path)
    {
        var c = await ReadyAsync();
        try { return await c.Storage.From(bucket).CreateSignedUrl(path, 3600); }
        catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("404")) { return null; }
    }

    /// <summary>Saves the session through the app's store, so a member stays signed in after closing the app.</summary>
    sealed class StoreSessionHandler(ISessionStore? store) : Supabase.Gotrue.Interfaces.IGotrueSessionPersistence<Session>
    {
        public void SaveSession(Session session) =>
            store?.Save(System.Text.Json.JsonSerializer.Serialize(new Saved(session.AccessToken ?? "", session.RefreshToken)));

        public void DestroySession() => store?.Clear();

        public Session? LoadSession()
        {
            if (store?.Load() is not { } json) return null;
            try
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(json);
                return saved is null ? null : new Session { AccessToken = saved.Access, RefreshToken = saved.Refresh };
            }
            catch (JsonException) { return null; }
        }

        record Saved(string Access, string? Refresh);
    }
}
