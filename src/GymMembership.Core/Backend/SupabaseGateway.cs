using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using GymMembership.Core.Models;
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
    readonly ISessionStore? _store;
    readonly SemaphoreSlim _startup = new(1, 1);
    bool _started;

    public SupabaseGateway(SupabaseSettings settings, ISessionStore? store = null)
    {
        _store = store;
        // HTTP/2 over one long-lived connection: on a lossy line (2026-11-23: 10-20% loss to Supabase) every new connection risks
        // 1-8 s handshake retries, so a page firing 15 requests over 15 fresh HTTP/1.1 connections stalled for up to 20 s.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always
        };
        var http = new HttpClient(new Http2(handler)) { Timeout = TimeSpan.FromSeconds(20) };
        _client = new Client(settings.Url, settings.AnonKey, new Supabase.SupabaseOptions
        {
            AutoRefreshToken = true,
            AutoConnectRealtime = false,
            HttpClient = http,
            StorageClientOptions = new Supabase.Storage.ClientOptions
            {
                HttpRequestClient = http,
                HttpRequestTimeout = TimeSpan.FromSeconds(15)
            },
            GotrueRetry = new Supabase.Core.Http.RetryOptions
            {
                MaxRetries = 2,
                BaseDelay = TimeSpan.FromMilliseconds(250),
                MaxDelay = TimeSpan.FromSeconds(2),
                UseJitter = true
            },
            SessionHandler = new StoreSessionHandler(store)
        });
    }

    public string? CurrentUserId => _client.Auth.CurrentUser?.Id;

    // for SupabaseRealtimeChat, which lives in this assembly: the connected client and the current sign-in token
    internal async Task<Client> ClientAsync() => await ReadyAsync();
    internal string? AccessToken => _client.Auth.CurrentSession?.AccessToken;

    // One Realtime socket shared by chat and the live records (F4): kept connected while signed in so navigating between pages
    // never drops and re-handshakes the WebSocket; TokenRefreshed updates SetAuth so the socket stays alive past the 1-hour JWT mark.
    readonly SemaphoreSlim _realtime = new(1, 1);
    int _realtimeHolders;
    bool _realtimeConnected;

    internal async Task<Supabase.Realtime.Interfaces.IRealtimeClient<Supabase.Realtime.RealtimeSocket, Supabase.Realtime.RealtimeChannel>> RealtimeAcquireAsync()
    {
        var rt = (await ReadyAsync()).Realtime;
        await _realtime.WaitAsync();
        try
        {
            if (AccessToken is { } token) rt.SetAuth(token);   // so the server filters changes by this user's row access
            if (!_realtimeConnected)
            {
                await rt.ConnectAsync();
                _realtimeConnected = true;
            }
            _realtimeHolders++;
            return rt;
        }
        finally { _realtime.Release(); }
    }

    internal async Task RealtimeReleaseAsync()
    {
        await ReadyAsync();
        await _realtime.WaitAsync();
        try
        {
            if (_realtimeHolders > 0) _realtimeHolders--;
        }
        finally { _realtime.Release(); }
    }

    async Task DisconnectRealtimeAsync()
    {
        await _realtime.WaitAsync();
        try
        {
            _realtimeHolders = 0;
            if (!_realtimeConnected) return;
            _realtimeConnected = false;
            try { _client.Realtime.Disconnect(); } catch { /* already gone */ }
        }
        finally { _realtime.Release(); }
    }

    async Task<Client> ReadyAsync()
    {
        if (_started) return _client;
        await _startup.WaitAsync();
        try
        {
            if (!_started)
            {
                await _client.InitializeAsync();
                _client.Realtime.AddStateChangedHandler((_, state) =>
                {
                    if (state is Supabase.Realtime.Constants.SocketState.Open) _realtimeConnected = true;
                    else if (state is Supabase.Realtime.Constants.SocketState.Close or Supabase.Realtime.Constants.SocketState.Error) _realtimeConnected = false;
                });
                _client.Auth.AddStateChangedListener((sender, state) =>
                {
                    if (state is Constants.AuthState.TokenRefreshed or Constants.AuthState.SignedIn)
                    {
                        if (_realtimeConnected && AccessToken is { } token)
                            try { _client.Realtime.SetAuth(token); } catch { /* reconnect will pick up latest token */ }
                    }
                    else if (state == Constants.AuthState.SignedOut)
                    {
                        _ = DisconnectRealtimeAsync();
                    }
                });
                _started = true;
            }
        }
        finally { _startup.Release(); }
        return _client;
    }

    // ---------- auth ----------
    public async Task SignInAsync(string email, string password)
    {
        var c = await ReadyAsync();
        await SignInThroughLockAsync(c, email, password);
    }

    // owner fixes 2026-11-19 Q2 (0103): through the sign-in function, which counts wrong passwords and locks the account for
    // 5 minutes after 10; it answers with the session, which the client then takes over
    async Task SignInThroughLockAsync(Supabase.Client c, string email, string password)
    {
        string json;
        try
        {
            json = await c.Functions.Invoke("sign-in", null, new Supabase.Functions.Client.InvokeFunctionOptions
                { Body = new Dictionary<string, object> { ["email"] = email.Trim(), ["password"] = password } });
        }
        catch (Exception ex) when (ErrorCode(ex.Message) is { } code) { throw new Exception(code, ex); }   // invalid_credentials, account_locked, ...
        var root = JsonDocument.Parse(json).RootElement;
        await c.Auth.SetSession(root.GetProperty("access_token").GetString()!, root.GetProperty("refresh_token").GetString()!);
    }

    public async Task SignUpAsync(string email, string password, string firstName, string lastName, string termsVersion)
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
                    ["username"] = email.Trim().ToLowerInvariant(), ["terms_version"] = termsVersion   // 0069
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
        // Supabase does not ask for the old password, so check it here; it is a sign-in, so it counts toward the lock (0103)
        try { await SignInThroughLockAsync(c, email, currentPassword); }
        catch (Exception ex) when (ex.Message == "invalid_credentials") { throw new Exception("wrong_password"); }
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
        ClearReadCache();
        try
        {
            var c = await ReadyAsync();
            await c.Auth.SignOut();
        }
        catch (Exception ex) when (ex is GotrueException or HttpRequestException or TaskCanceledException or TimeoutException)
        {
            // Offline or transient network failure during logout: still clear the local session so the user can return to login.
            _store?.Clear();
            try { await _client.Auth.SignOut(Constants.SignOutScope.Local); } catch { }
        }
    }

    public async Task<bool> RestoreSessionAsync()
    {
        var c = await ReadyAsync();
        var session = await c.Auth.RetrieveSessionAsync();
        return session?.User is not null;
    }

    // ---------- tables ----------
    // Coalesce concurrent identical unfiltered reads and briefly cache slow-changing reference/directory tables across page switches.
    // Every write/RPC invalidates the cache immediately so the user never sees stale data after making a change.
    static readonly TimeSpan ReferenceCacheTtl = TimeSpan.FromSeconds(10);
    static readonly HashSet<Type> CacheableTables = [typeof(Profile), typeof(PublicProfile), typeof(Role), typeof(GymSettings), typeof(MembershipPackage), typeof(Amenity)];
    readonly ConcurrentDictionary<Type, (object Rows, DateTime Until)> _readCache = new();
    readonly ConcurrentDictionary<Type, Task<object>> _inFlightReads = new();
    long _cacheEpoch;

    void ClearReadCache()
    {
        Interlocked.Increment(ref _cacheEpoch);
        _readCache.Clear();
    }

    // Retry idempotent reads on transient socket/timeout drops (stale keep-alive connections, brief network blips).
    static async Task<T> ReadWithRetryAsync<T>(Func<Task<T>> call)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await call(); }
            catch (Exception ex) when (attempt < 2 && ex is HttpRequestException or TaskCanceledException or TimeoutException)
            {
                await Task.Delay(attempt == 0 ? 150 : 450);
            }
        }
    }

    // ponytail: the library doesn't quote values inside a grouped filter (a && b), so a comma starts a new condition. Filters hold only
    // ids and fixed words today; send typed text to a server function instead. QueryInjectionTests pins this.
    public async Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new()
    {
        var c = await ReadyAsync();
        if (where is null)
        {
            var type = typeof(T);
            if (CacheableTables.Contains(type) && _readCache.TryGetValue(type, out var hit) && hit.Until > DateTime.UtcNow)
                return (IReadOnlyList<T>)hit.Rows;

            var epoch = Volatile.Read(ref _cacheEpoch);
            var task = _inFlightReads.GetOrAdd(type, _ => FetchUnfilteredAsync<T>(c, type, epoch));
            return (IReadOnlyList<T>)await task;
        }
        return await ReadWithRetryAsync(async () => (IReadOnlyList<T>)(await c.From<T>().Where(where).Get()).Models);
    }

    async Task<object> FetchUnfilteredAsync<T>(Client c, Type type, long epoch) where T : BaseModel, new()
    {
        try
        {
            var rows = await ReadWithRetryAsync(async () => (IReadOnlyList<T>)(await c.From<T>().Get()).Models);
            if (CacheableTables.Contains(type) && Volatile.Read(ref _cacheEpoch) == epoch)
                _readCache[type] = (rows, DateTime.UtcNow + ReferenceCacheTtl);
            return rows;
        }
        finally { _inFlightReads.TryRemove(type, out _); }
    }

    public async Task<T> InsertAsync<T>(T row) where T : BaseModel, new()
    {
        ClearReadCache();
        var c = await ReadyAsync();
        var response = await c.From<T>().Insert(row);
        return response.Models.FirstOrDefault() ?? row;
    }

    public async Task UpdateAsync<T>(T row) where T : BaseModel, new()
    {
        ClearReadCache();
        var c = await ReadyAsync();
        await c.From<T>().Update(row);
    }

    public async Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new()
    {
        ClearReadCache();
        var c = await ReadyAsync();
        // the library wants the column as Func<T, object>; boxing the member access keeps the column name it reads from the expression
        var boxed = Expression.Lambda<Func<T, object>>(Expression.Convert(column.Body, typeof(object)), column.Parameters);
        await c.From<T>().Where(where).Set(boxed, value).Update();
    }

    // ---------- server functions ----------
    public async Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null)
    {
        ClearReadCache();
        var c = await ReadyAsync();
        return (await c.Rpc<TResult>(function, args))!;
    }

    public async Task RpcAsync(string function, Dictionary<string, object>? args = null)
    {
        ClearReadCache();
        var c = await ReadyAsync();
        await c.Rpc(function, args);
    }

    public async Task<string> InvokeFunctionAsync(string name, Dictionary<string, object> body)
    {
        ClearReadCache();
        var c = await ReadyAsync();
        // the signed-in staff member's own token goes along, and the function checks who it is (supabase/functions/_shared/admin.ts)
        try { return await c.Functions.Invoke(name, c.Auth.CurrentSession?.AccessToken, new Supabase.Functions.Client.InvokeFunctionOptions { Body = body }); }
        catch (Exception ex) when (ErrorCode(ex.Message) is { } code) { throw new Exception(code, ex); }   // {"error":"email_taken"} -> email_taken
    }

    public async Task<bool> EmailInUseAsync(string email)
    {
        var c = await ReadyAsync();
        try
        {
            var json = await c.Functions.Invoke("check-email", null, new Supabase.Functions.Client.InvokeFunctionOptions { Body = new Dictionary<string, object> { ["email"] = email } });
            return JsonDocument.Parse(json).RootElement.GetProperty("in_use").GetBoolean();
        }
        catch (Exception ex) when (ErrorCode(ex.Message) is { } code) { throw new Exception(code, ex); }
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
        try { return await ReadWithRetryAsync(() => c.Storage.From(bucket).CreateSignedUrl(path, 3600)); }
        catch (Exception ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("404")) { return null; }
    }

    // the Supabase library builds its own HttpRequestMessage (HTTP/1.1 by default), so HttpClient.DefaultRequestVersion never reaches it
    sealed class Http2(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Version = System.Net.HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            return base.SendAsync(request, ct);
        }
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
