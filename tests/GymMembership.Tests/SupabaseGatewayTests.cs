using System.Net;
using System.Text;
using System.Text.Json;
using GymMembership.Core.Backend;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>
/// There is no Supabase project to talk to yet, so these start a small fake one and check the exact requests the gateway sends:
/// the filters on reads, the columns on writes (which must fit the table privileges in 0006_rls.sql), function calls, uploads,
/// sign-in, and how server errors come back. They show the gateway speaks the right protocol, not that the project accepts it.
/// </summary>
public sealed class SupabaseGatewayTests : IDisposable
{
    sealed record Seen(string Method, string Path, string Query, string Body, Dictionary<string, string> Headers);

    readonly HttpListener _http = new();
    readonly List<Seen> _seen = new();
    readonly string _url;
    Func<Seen, (int Status, string Json)> _reply = _ => (200, "[]");

    public SupabaseGatewayTests()
    {
        var port = new Random().Next(20000, 40000);
        _url = $"http://localhost:{port}";
        _http.Prefixes.Add(_url + "/");
        _http.Start();
        _ = Task.Run(async () =>
        {
            while (_http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _http.GetContextAsync(); } catch { return; }
                var body = await new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                var headers = ctx.Request.Headers.AllKeys.ToDictionary(k => k!, k => ctx.Request.Headers[k]!, StringComparer.OrdinalIgnoreCase);
                var seen = new Seen(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, Uri.UnescapeDataString(ctx.Request.Url.Query), body, headers);
                lock (_seen) _seen.Add(seen);
                var (status, json) = _reply(seen);
                var bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _http.Close();

    Seen Last { get { lock (_seen) return _seen.Last(); } }

    static string Jwt() // header.payload.signature, with an expiry far away so the client keeps it
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"HS256\",\"typ\":\"JWT\"}")}.{B64("{\"sub\":\"u-1\",\"exp\":4102444800,\"role\":\"authenticated\"}")}.sig";
    }

    static string SessionJson(string email = "ana@demo.io") =>
        """{"access_token":"__JWT__","token_type":"bearer","expires_in":3600,"refresh_token":"refresh-1","user":{"id":"u-1","email":"__EMAIL__","aud":"authenticated","role":"authenticated","app_metadata":{},"user_metadata":{}}}"""
            .Replace("__JWT__", Jwt()).Replace("__EMAIL__", email);

    async Task<SupabaseGateway> SignedInAsync()
    {
        var gw = new SupabaseGateway(new SupabaseSettings(_url, "anon-key"));
        _reply = s => s.Path.Contains("/auth/v1/token") ? (200, SessionJson()) : (200, "[]");
        await gw.SignInAsync("ana@demo.io", "secret12");
        return gw;
    }

    // ---------- settings ----------
    [Theory]
    [InlineData("""{"url":"https://abc.supabase.co/","anonKey":"eyJ"}""", true)]
    [InlineData("""{"url":"https://abc.supabase.co","anonKey":"PASTE_YOUR_ANON_KEY"}""", false)]
    [InlineData("""{"url":"","anonKey":"eyJ"}""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void Settings_file_is_used_only_when_it_is_filled_in(string? json, bool usable)
    {
        var s = SupabaseSettings.Parse(json);
        Assert.Equal(usable, s is not null);
        if (usable) Assert.Equal("https://abc.supabase.co", s!.Url);   // trailing slash removed
    }

    // ---------- auth ----------
    [Fact]
    public async Task Sign_in_posts_the_credentials_with_the_anon_key_and_keeps_the_user_id()
    {
        var gw = await SignedInAsync();
        var login = _seen.First(s => s.Path.Contains("/auth/v1/token"));
        Assert.Equal("POST", login.Method);
        Assert.Contains("grant_type=password", login.Query);
        Assert.Equal("ana@demo.io", JsonDocument.Parse(login.Body).RootElement.GetProperty("email").GetString());
        Assert.Equal("anon-key", login.Headers["apikey"]);
        Assert.Equal("u-1", gw.CurrentUserId);
    }

    [Fact]
    public async Task Sign_up_sends_the_name_and_username_the_database_trigger_reads()
    {
        var gw = new SupabaseGateway(new SupabaseSettings(_url, "anon-key"));
        _reply = _ => (200, SessionJson("new@demo.io"));
        await gw.SignUpAsync(" New@Demo.io ", "secret12", "New Person");
        var signUp = _seen.First(s => s.Path.EndsWith("/auth/v1/signup"));
        var root = JsonDocument.Parse(signUp.Body).RootElement;
        Assert.Equal("new@demo.io", root.GetProperty("email").GetString()!.ToLowerInvariant());
        Assert.Equal("New Person", root.GetProperty("data").GetProperty("full_name").GetString());
        Assert.Equal("new@demo.io", root.GetProperty("data").GetProperty("username").GetString());
    }

    [Fact]
    public async Task A_taken_email_comes_back_as_email_taken()
    {
        var gw = new SupabaseGateway(new SupabaseSettings(_url, "anon-key"));
        _reply = _ => (422, """{"code":422,"error_code":"user_already_exists","msg":"User already registered"}""");
        var result = await Safe.RunAsync(async () => { await gw.SignUpAsync("a@b.io", "secret12", "A"); return true; });
        Assert.Equal(AppErrorKind.Validation, result.Error);
        Assert.Equal("email_taken", result.Message);
    }

    [Fact]
    public async Task A_bad_password_is_a_failed_sign_in_the_app_reports_as_unauthorized()
    {
        var gw = new SupabaseGateway(new SupabaseSettings(_url, "anon-key"));
        _reply = _ => (400, """{"code":400,"error_code":"invalid_credentials","msg":"Invalid login credentials"}""");
        var result = await Safe.RunAsync(async () => { await gw.SignInAsync("a@b.io", "wrong"); return true; });
        Assert.Equal(AppErrorKind.Unauthorized, result.Error);
    }

    // ---------- reads ----------
    [Fact]
    public async Task A_read_filters_on_the_server_and_sends_the_users_token()
    {
        var gw = await SignedInAsync();
        var uid = gw.CurrentUserId;
        _reply = _ => (200, """[{"payment_id":7,"payment_code":"GYM-AB2CD","kind":"membership","method":"qr","status":"pending","amount":1500,"discount_amount":0,"member_id":"u-1","created_at":"2026-10-12T01:00:00+00:00"}]""");
        var rows = await gw.ListAsync<Payment>(p => p.MemberId == uid && p.Status == "pending");

        Assert.Equal("/rest/v1/payments", Last.Path);
        Assert.Contains("member_id.eq.u-1", Last.Query);   // the library groups two conditions as and=(a.eq.x,b.eq.y)
        Assert.Contains("status.eq.pending", Last.Query);
        Assert.StartsWith("Bearer " + Jwt()[..20], Last.Headers["Authorization"]);   // RLS decides what comes back from this
        Assert.Equal(("GYM-AB2CD", 1500m, 7), (rows[0].PaymentCode, rows[0].Amount, rows[0].Id));
    }

    // ---------- writes: only columns the table privileges allow ----------
    static HashSet<string> Columns(string body)
    {
        var root = JsonDocument.Parse(body).RootElement;
        return (root.ValueKind == JsonValueKind.Array ? root[0] : root).EnumerateObject().Select(p => p.Name).ToHashSet();
    }

    [Fact]
    public async Task Posting_a_shift_sends_only_the_four_columns_a_coach_may_insert()
    {
        var gw = await SignedInAsync();
        _reply = _ => (201, """[{"shift_post_id":1,"coach_id":"u-1","starts_at":"2026-10-20T01:00:00+00:00","ends_at":"2026-10-20T05:00:00+00:00","note":"x","status":"scheduled"}]""");
        await gw.InsertAsync(new ShiftPost { CoachId = "u-1", Start = DateTime.UtcNow, End = DateTime.UtcNow.AddHours(4), Note = "x" });
        Assert.Equal("POST", Last.Method);
        Assert.Equal(new[] { "coach_id", "ends_at", "note", "starts_at" }, Columns(Last.Body).Order());
    }

    [Fact]
    public async Task Sending_a_message_never_sets_the_time_or_read_flag()
    {
        var gw = await SignedInAsync();
        _reply = _ => (201, """[{"message_id":1,"coach_id":"c","member_id":"m","sender_id":"m","body":"hi","sent_at":"2026-10-12T01:00:00+00:00"}]""");
        await gw.InsertAsync(new ChatMessage { CoachId = "c", MemberId = "m", SenderId = "m", Body = "hi" });
        Assert.Equal(new[] { "body", "coach_id", "member_id", "sender_id" }, Columns(Last.Body).Order());
    }

    [Fact]
    public async Task Logging_an_amenity_visit_cannot_set_its_own_status()
    {
        var gw = await SignedInAsync();
        _reply = _ => (201, """[{"amenity_usage_id":1,"user_amenity_id":4,"used_at":"2026-10-12T01:00:00+00:00","status":"pending"}]""");
        await gw.InsertAsync(new AmenityUsage { UserAmenityId = 4, ProofPath = "u-1/x.png" });
        Assert.DoesNotContain("status", Columns(Last.Body));
        Assert.DoesNotContain("used_at", Columns(Last.Body));
    }

    [Fact]
    public async Task Updating_a_shift_filters_by_its_id_and_leaves_the_owner_alone()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, "[]");
        await gw.UpdateAsync(new ShiftPost { Id = 5, CoachId = "u-1", Start = DateTime.UtcNow, End = DateTime.UtcNow.AddHours(2), Status = "cancelled" });
        Assert.Equal("PATCH", Last.Method);
        Assert.Contains("shift_post_id=eq.5", Last.Query);
        Assert.DoesNotContain("coach_id", Columns(Last.Body));
        Assert.DoesNotContain("shift_post_id", Columns(Last.Body));
    }

    [Fact]
    public async Task Marking_a_notification_read_changes_only_read_at()
    {
        var gw = await SignedInAsync();
        await gw.UpdateAsync(new UserNotification { Id = 3, UserId = "u-1", Type = "payment", Title = "Payment approved", ReadAt = DateTime.UtcNow });
        Assert.Equal(new[] { "read_at" }, Columns(Last.Body));
        Assert.Contains("notification_id=eq.3", Last.Query);
    }

    [Fact]
    public async Task Editing_an_amenity_in_the_catalog_never_sends_the_status_columns()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, "[]");
        await gw.UpdateAsync(new Amenity { Id = 4, Name = "Pool", Price = 300, IsActive = true, Availability = "unavailable", StatusReason = "x", BackAt = DateTime.UtcNow });
        var columns = Columns(Last.Body);
        Assert.DoesNotContain("status", columns);
        Assert.DoesNotContain("status_reason", columns);
        Assert.DoesNotContain("back_at", columns);
        Assert.Contains("amenity_id=eq.4", Last.Query);
    }

    [Fact]
    public async Task Setting_a_status_calls_the_function_and_reads_back_who_was_alerted()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, """[{"member_id":"u-9","full_name":"Alex Tan"}]""");
        var told = (await new AmenityService(gw).SetStatusAsync(4, "unavailable", "Cleaning", new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc))).Value!;

        Assert.Equal("/rest/v1/rpc/set_amenity_status", Last.Path);
        var body = Last.Body;
        Assert.Contains("\"p_amenity_id\":4", body);
        Assert.Contains("\"p_status\":\"unavailable\"", body);
        Assert.Contains("\"p_reason\":\"Cleaning\"", body);
        Assert.Contains("2026-10-20T02:00:00", body);
        Assert.Equal("u-9", told[0].MemberId);
        Assert.Equal("Alex Tan", told[0].Name);
    }

    [Fact]
    public async Task Marking_every_unread_notification_read_is_one_request_that_leaves_read_ones_alone()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, "[]");
        await gw.UpdateWhereAsync<UserNotification, DateTime?>(n => n.UserId == "u-1" && n.ReadAt == null, n => n.ReadAt, DateTime.UtcNow);
        Assert.Equal("PATCH", Last.Method);
        // a compound filter goes out as one grouped "and=(...)"; both conditions must be there
        Assert.Contains("user_id.eq.u-1", Last.Query);
        Assert.Contains("read_at.is.null", Last.Query);   // without this every already-read alert would get a new read time
        Assert.Equal(new[] { "read_at" }, Columns(Last.Body));
    }

    [Fact]
    public async Task A_coach_profile_update_sends_the_four_editable_fields_and_filters_on_their_own_id()
    {
        var gw = await SignedInAsync();
        await gw.UpdateAsync(new Coach { UserId = "u-1", Bio = "b", Specialty = "s", HourlyRate = 800, IsAvailable = true });
        Assert.Equal(new[] { "bio", "hourly_rate", "is_available", "specialty" }, Columns(Last.Body).Order());
        Assert.Contains("user_id=eq.u-1", Last.Query);
        Assert.DoesNotContain("user_id", Columns(Last.Body));   // coaches may not change their own key column
    }

    [Fact]
    public async Task Saving_the_theme_and_the_gym_settings_never_send_their_key_column()
    {
        var gw = await SignedInAsync();
        await gw.UpdateAsync(new UserSettings { UserId = "u-1", Theme = "dark" });
        Assert.Equal(new[] { "language", "theme", "timezone" }, Columns(Last.Body).Order());
        Assert.Contains("user_id=eq.u-1", Last.Query);

        await gw.UpdateAsync(new GymSettings { GymName = "Iron House", WalkInPrice = 200 });
        Assert.DoesNotContain("id", Columns(Last.Body));
        Assert.Contains("id=eq.1", Last.Query);
    }

    [Fact]
    public async Task Deleting_a_package_filters_on_its_id()
    {
        var gw = await SignedInAsync();
        await gw.DeleteAsync(new MembershipPackage { Id = 3, Name = "x" });
        Assert.Equal("DELETE", Last.Method);
        Assert.Contains("membership_package_id=eq.3", Last.Query);
    }

    // ---------- server functions ----------
    [Fact]
    public async Task A_function_that_returns_a_payment_is_called_by_name_and_read_back()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, """{"payment_id":9,"payment_code":"GYM-7F3K2","kind":"membership","method":"qr","status":"pending","amount":4000,"discount_amount":0,"member_id":"u-1","expires_at":"2026-10-14T01:00:00+00:00","created_at":"2026-10-12T01:00:00+00:00"}""");
        var pay = await gw.RpcAsync<Payment>("avail_membership", new() { ["p_package_id"] = 2, ["p_discount_code"] = "WELCOME10" });

        Assert.Equal("/rest/v1/rpc/avail_membership", Last.Path);
        var args = JsonDocument.Parse(Last.Body).RootElement;
        Assert.Equal(2, args.GetProperty("p_package_id").GetInt32());
        Assert.Equal("WELCOME10", args.GetProperty("p_discount_code").GetString());
        Assert.Equal(("GYM-7F3K2", 4000m, 9), (pay.PaymentCode, pay.Amount, pay.Id));
        Assert.NotNull(pay.ExpiresAt);
    }

    [Fact]
    public async Task Reports_come_back_as_typed_rows_and_a_number_function_returns_a_number()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, """[{"day":"2026-10-11","kind":"membership","method":"cash","payments":3,"total":4500.00}]""");
        var rows = await gw.RpcAsync<List<RevenueRow>>("revenue_summary", new() { ["p_from"] = "2026-10-01", ["p_to"] = "2026-10-12" });
        Assert.Equal(("membership", "cash", 3L, 4500m), (rows[0].Kind, rows[0].Method, rows[0].Payments, rows[0].Total));

        _reply = _ => (200, "42");
        Assert.Equal(42, await gw.RpcAsync<int>("hire_coach", new() { ["p_coach_id"] = "c-1" }));
    }

    [Theory]
    [InlineData(403, """{"code":"42501","message":"forbidden"}""", AppErrorKind.Unauthorized)]
    [InlineData(400, """{"code":"22023","message":"invalid_code"}""", AppErrorKind.Validation)]
    [InlineData(409, """{"code":"P0002","message":"already_availed"}""", AppErrorKind.Conflict)]
    [InlineData(409, """{"code":"23505","message":"duplicate key value violates unique constraint \"payments_reference_no_key\""}""", AppErrorKind.Conflict)]
    [InlineData(409, """{"code":"23P01","message":"conflicting key value violates exclusion constraint \"training_sessions_no_coach_overlap\""}""", AppErrorKind.Conflict)]
    [InlineData(409, """{"code":"23503","message":"update or delete on table \"membership_packages\" violates foreign key constraint"}""", AppErrorKind.Conflict)]
    public async Task Database_errors_come_back_as_the_kind_the_app_shows(int status, string json, AppErrorKind expected)
    {
        var gw = await SignedInAsync();
        _reply = _ => (status, json);
        var result = await Safe.RunAsync(async () => { await gw.RpcAsync("avail_membership", new() { ["p_package_id"] = 1 }); return true; });
        Assert.Equal(expected, result.Error);
    }

    [Fact]
    public async Task Edge_function_calls_carry_the_signed_in_users_token_and_the_body()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, """{"id":"u-9"}""");
        await gw.InvokeFunctionAsync("create-user", new() { ["name"] = "Rey", ["email"] = "rey@demo.io", ["role"] = "coach", ["password"] = "temp1234" });

        Assert.Equal("/functions/v1/create-user", Last.Path);
        Assert.Equal("Bearer " + Jwt(), Last.Headers["Authorization"]);
        Assert.Equal("coach", JsonDocument.Parse(Last.Body).RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_taken_email_from_the_create_user_function_is_reported_in_plain_words()
    {
        var gw = await SignedInAsync();
        _reply = _ => (409, """{"error":"email_taken"}""");
        var result = await new AdminService(gw).CreateUserAsync("Rey", "rey@demo.io", "coach", "temp1234");
        Assert.Equal(AppErrorKind.Validation, result.Error);
        Assert.Equal("Someone already uses that email.", ErrorText.For(result.Error, result.Message?.Contains("email_taken") == true ? "email_taken" : result.Message));
    }

    // ---------- files ----------
    [Fact]
    public async Task An_upload_goes_to_the_bucket_with_a_safe_name_and_returns_the_path_inside_it()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, """{"Key":"payment-proofs/u-1/7-my_slip__1_.png","Id":"abc"}""");
        var path = await gw.UploadAsync("payment-proofs", "u-1/7-my slip (1).png", [1, 2, 3]);

        Assert.Equal("u-1/7-my_slip__1_.png", path);                       // spaces and brackets replaced
        Assert.Equal("/storage/v1/object/payment-proofs/u-1/7-my_slip__1_.png", Last.Path);
        Assert.Equal("image/png", Last.Headers["Content-Type"].Split(';')[0]);
    }

    [Fact]
    public async Task A_signed_link_is_asked_for_by_bucket_and_path()
    {
        var gw = await SignedInAsync();
        _reply = _ => (200, """{"signedURL":"/object/sign/gym-assets/qr.png?token=t1"}""");
        var url = await gw.SignedUrlAsync("gym-assets", "qr.png");
        Assert.Equal("/storage/v1/object/sign/gym-assets/qr.png", Last.Path);
        Assert.Contains("token=t1", url);
    }

    // ---------- staying signed in ----------
    sealed class MemoryStore : ISessionStore
    {
        public string? Saved;
        public string? Load() => Saved;
        public void Save(string json) => Saved = json;
        public void Clear() => Saved = null;
    }

    [Fact]
    public async Task A_saved_session_signs_the_member_in_again_and_signing_out_forgets_it()
    {
        var store = new MemoryStore();
        _reply = s => s.Path.Contains("/auth/v1/token") ? (200, SessionJson()) : (200, "[]");
        var first = new SupabaseGateway(new SupabaseSettings(_url, "anon-key"), store);
        await first.SignInAsync("ana@demo.io", "secret12");
        Assert.NotNull(store.Saved);                                   // written to the device

        _reply = s => s.Path.Contains("/auth/v1/user") ? (200, """{"id":"u-1","email":"ana@demo.io","aud":"authenticated"}""") : s.Path.Contains("/auth/v1/token") ? (200, SessionJson()) : (200, "[]");
        var relaunched = new SupabaseGateway(new SupabaseSettings(_url, "anon-key"), store);
        Assert.True(await relaunched.RestoreSessionAsync());
        Assert.Equal("u-1", relaunched.CurrentUserId);

        await relaunched.SignOutAsync();
        Assert.Null(store.Saved);
    }

    [Fact]
    public async Task With_nothing_saved_there_is_nobody_to_restore()
    {
        var gw = new SupabaseGateway(new SupabaseSettings(_url, "anon-key"), new MemoryStore());
        Assert.False(await gw.RestoreSessionAsync());
    }

    // ---------- every filter the services use must be one the library can send ----------
    [Fact]
    public async Task Every_filter_the_services_use_translates_to_a_server_query()
    {
        var gw = await SignedInAsync();
        var results = new List<(string Name, bool Ok, string? Why)>();
        void Note(string name, dynamic r) => results.Add((name, r.Ok, r.Message));

        var items = await new PaymentStaffService(gw).ItemsAsync(PayKind.Renewal, "m-1");   // member, and (active or expired)
        Note("renewal items", items);
        var query = _seen.Last(r => r.Path.EndsWith("/user_membership_packages")).Query;
        Assert.Contains("member_id.eq.m-1", query);
        Assert.Contains("status.eq.active", query);
        Assert.Contains("status.eq.expired", query);

        Note("coaches", await new CoachService(gw).ListCoachesAsync());
        Note("my coach", await new CoachService(gw).MyCoachAsync());
        Note("my shifts", await new CoachService(gw).MyShiftsAsync());
        Note("access", await new MembershipService(gw).AccessAsync());
        Note("my payments", await new MembershipService(gw).MyPaymentsAsync());
        Note("my memberships", await new MembershipService(gw).MyMembershipsAsync());
        Note("messages", await new MessageService(gw).MessagesAsync("c-1", "m-1"));
        Note("find members", await new AttendanceService(gw).FindMembersAsync("a"));
        Note("requests", await new SessionService(gw).RequestsAsync());
        Note("sessions", await new SessionService(gw).SessionsAsync());
        Note("amenities", await new AmenityService(gw).CatalogAsync());
        Note("pending payments", await new PaymentStaffService(gw).PendingAsync());
        Note("notifications", await new NotificationService(gw).ListAsync());
        Note("discounts", await new AdminService(gw).DiscountsAsync());

        Assert.True(results.All(r => r.Ok), string.Join("; ", results.Where(r => !r.Ok).Select(r => $"{r.Name}: {r.Why}")));
    }
}
