using System.Linq.Expressions;
using System.Reflection;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Demo;

/// <summary>
/// In-memory stand-in for Supabase so the app can be explored, and tested, without a database.
/// It follows the same rules as supabase/migrations: which rows each role can see (0006_rls.sql), which columns can be written
/// directly, and the server-side functions (0005_functions.sql). Error messages are the same tokens the SQL raises.
/// Demo logins: admin@ / employee@ / coach@ / kai@ / member@ / bea@ / carlo@ demo.io, password "demo1234".
/// </summary>
public sealed class DemoGateway : ISupabaseGateway
{
    public const string DemoPassword = "demo1234";

    // plan 5.2
    static readonly Dictionary<string, string[]> RolePermissions = new()
    {
        ["admin"] = ["payments:verify", "payments:record", "payments:void", "check_ins:record", "amenity_usages:verify", "amenities:set_status", "support:reply", "users:manage", "roles:manage",
                     "catalog:manage", "discounts:manage", "gym_settings:manage", "reports:revenue", "reports:cash", "audit:read"],
        ["employee"] = ["payments:verify", "payments:record", "check_ins:record", "amenity_usages:verify", "amenities:set_status", "support:reply", "reports:cash"],
        ["coach"] = ["sessions:manage", "shifts:manage", "attendance:record"],
        ["member"] = []
    };

    readonly Dictionary<Type, List<object>> _tables = new();
    readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);   // email -> user id
    readonly Dictionary<string, string> _passwords = new();                                  // only accounts whose password changed; the rest use DemoPassword
    int _receiptSeq;

    public string? CurrentUserId { get; private set; }
    public Exception? FailWith { get; set; }

    public DemoGateway(bool seed = true) { if (seed) Seed(); }

    // ---------- storage helpers ----------
    List<object> Table(Type t) => _tables.TryGetValue(t, out var l) ? l : _tables[t] = new();
    IEnumerable<T> All<T>() => Table(typeof(T)).Cast<T>();
    static PropertyInfo Pk(Type t) => t.GetProperties().First(p => p.GetCustomAttribute<PrimaryKeyAttribute>() != null);
    static T Clone<T>(T row) => (T)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(row, null)!;

    T Add<T>(T row) where T : class
    {
        var pk = Pk(typeof(T));
        if (pk.PropertyType == typeof(int) && (int)pk.GetValue(row)! == 0)
            pk.SetValue(row, All<T>().Select(r => (int)pk.GetValue(r)!).DefaultIfEmpty(0).Max() + 1);
        Table(typeof(T)).Add(row);
        return row;
    }

    static Exception Err(string message) => new(message);

    // ---------- who is calling ----------
    string Uid => CurrentUserId ?? throw Err("not authenticated");
    string[] RolesOf(string? user) => All<UserRole>().Where(u => u.UserId == user)
        .Select(u => All<Role>().First(r => r.RoleId == u.RoleId).Name).ToArray();
    bool Has(string permission) => RolesOf(CurrentUserId).Any(r => RolePermissions[r].Contains(permission));
    bool IsStaff => RolesOf(CurrentUserId).Any(r => r is "admin" or "employee");
    bool IsActiveUser => All<Profile>().FirstOrDefault(p => p.Id == CurrentUserId)?.IsActive ?? false;
    void RequireActive() { if (!IsActiveUser) throw Err("forbidden"); }
    void Require(string permission) { RequireActive(); if (!Has(permission)) throw Err("forbidden"); }

    static bool Running(UserMembershipPackage m) => m.Status == Status.Active && m.EndsAt > DateTime.UtcNow;
    bool ActiveMember(string? userId) => All<UserMembershipPackage>().Any(m => m.MemberId == userId && Running(m));
    bool CoachesMember(string coach, string? member) => All<CoachHire>().Any(h => h.CoachId == coach && h.MemberId == member && h.Status == Status.Active);
    bool IsCoach(string? userId) => All<Coach>().Any(c => c.UserId == userId);

    /// <summary>The demo's stand-in for the server sending someone an alert (a client cannot insert one itself, here or on the real table).</summary>
    public void Notify(string? userId, string type, string title, string? body = null)
    { if (userId is not null) Add(new UserNotification { UserId = userId, Type = type, Title = title, Body = body, CreatedAt = DateTime.UtcNow }); }
    // which chat an alert is about, so opening that chat clears only its alerts (the database keeps this in the alert's reference)
    readonly Dictionary<int, string> _chatAlerts = new();
    void NotifyChat(string userId, string title, string? body, string key)
    { var n = Add(new UserNotification { UserId = userId, Type = "message", Title = title, Body = body, CreatedAt = DateTime.UtcNow }); _chatAlerts[n.Id] = key; }
    void ClearChatAlerts(string userId, string key)
    { foreach (var n in All<UserNotification>().Where(n => n.UserId == userId && n.Type == "message" && n.ReadAt is null && _chatAlerts.GetValueOrDefault(n.Id) == key)) n.ReadAt = DateTime.UtcNow; }

    void NotifyStaff(string type, string title) { foreach (var id in All<UserRole>().Where(u => RolesOf(u.UserId).Any(r => r is "admin" or "employee")).Select(u => u.UserId).Distinct().ToList()) Notify(id, type, title); }
    void Audit(string action, string entity, string entityId) =>
        Add(new AuditEntry { ActorId = CurrentUserId, Action = action, Entity = entity, EntityId = entityId, CreatedAt = DateTime.UtcNow });

    // ---------- auth ----------
    public Task SignInAsync(string email, string password)
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        if (!_accounts.TryGetValue(email.Trim(), out var uid) || password != _passwords.GetValueOrDefault(uid, DemoPassword))
            return Task.FromException(Err("Invalid login credentials"));
        CurrentUserId = uid;   // like Supabase Auth, a deactivated user still signs in: the app checks profiles.is_active afterwards
        return Task.CompletedTask;
    }

    public Task SignUpAsync(string email, string password, string fullName)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(fullName) || !email.Contains('@') || password.Length < 6) return Task.FromException(Err("bad_input"));
        if (_accounts.ContainsKey(email)) return Task.FromException(Err("email_taken"));
        var id = AddUser("u-" + Guid.NewGuid().ToString("N")[..8], fullName.Trim(), email, "member");
        _passwords[id] = password;
        CurrentUserId = id;
        return Task.CompletedTask;
    }

    public Task ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var uid = Uid;
        if (currentPassword != _passwords.GetValueOrDefault(uid, DemoPassword)) return Task.FromException(Err("wrong_password"));
        if (newPassword.Length < 6) return Task.FromException(Err("bad_input"));
        _passwords[uid] = newPassword;
        return Task.CompletedTask;
    }

    public Task SignOutAsync() { CurrentUserId = null; return Task.CompletedTask; }
    public Task<bool> RestoreSessionAsync() => Task.FromResult(false);

    // ---------- Edge Functions: create-user and reset-password (plan 4.3, 4.4) ----------
    public Task InvokeFunctionAsync(string name, Dictionary<string, object> body)
    {
        try
        {
            if (FailWith is not null) throw FailWith;
            if (!RolesOf(Uid).Contains("admin") || !IsActiveUser) throw Err("forbidden");
            string S(string k) => Convert.ToString(body.GetValueOrDefault(k)) ?? "";
            switch (name)
            {
                case "create-user":
                {
                    var email = S("email").Trim().ToLowerInvariant(); var role = S("role");
                    if (role is not ("member" or "coach" or "employee") || string.IsNullOrWhiteSpace(S("name")) || !email.Contains('@') || S("password").Length < 6) throw Err("bad_input");
                    if (_accounts.ContainsKey(email)) throw Err("email_taken");
                    var id = AddUser("u-" + Guid.NewGuid().ToString("N")[..8], S("name").Trim(), email, role);
                    _passwords[id] = S("password");
                    All<Profile>().First(p => p.Id == id).MustChangePassword = true;
                    Audit("user.create", "profiles", id);
                    break;
                }
                case "reset-password":
                {
                    var p = All<Profile>().FirstOrDefault(x => x.Id == S("user_id")) ?? throw Err("not_found");
                    if (S("password").Length < 6) throw Err("bad_input");
                    _passwords[p.Id] = S("password"); p.MustChangePassword = true;
                    Notify(p.Id, "system", "Your password was reset. Sign in with the new one.");
                    Audit("user.reset_password", "profiles", p.Id);
                    break;
                }
                default: throw Err("not_found");
            }
            return Task.CompletedTask;
        }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    // ---------- table access (RLS-shaped, plan 6) ----------
    bool Allowed(object row)
    {
        var uid = CurrentUserId;
        if (uid is null) return false;
        if (!IsActiveUser) return row is Profile self && self.Id == uid;   // a deactivated user can only see their own profile
        var staff = IsStaff;
        return row switch
        {
            Profile p => p.Id == uid || staff || IsCoach(p.Id)
                || All<CoachHire>().Any(h => (h.CoachId == uid && h.MemberId == p.Id) || (h.MemberId == uid && h.CoachId == p.Id)),
            Member m => m.UserId == uid || staff || All<CoachHire>().Any(h => h.CoachId == uid && h.MemberId == m.UserId),
            Payment p => p.MemberId == uid || staff,
            UserMembershipPackage m => m.MemberId == uid || staff,
            MembershipRenewal r => staff || All<UserMembershipPackage>().Any(m => m.Id == r.UserMembershipPackageId && m.MemberId == uid),
            UserAmenity a => a.MemberId == uid || staff,
            AmenityUsage u => staff || All<UserAmenity>().Any(a => a.Id == u.UserAmenityId && a.MemberId == uid),
            UserNotification n => n.UserId == uid,
            UserSettings s => s.UserId == uid,
            UserRole r => r.UserId == uid || RolesOf(uid).Contains("admin"),
            AuditEntry => Has("audit:read"),
            CoachHire h => h.MemberId == uid || h.CoachId == uid || staff,
            TrainingSession s => s.MemberId == uid || s.CoachId == uid || staff,
            SessionAttendance a => staff || All<TrainingSession>().Any(s => s.Id == a.SessionId && (s.CoachId == uid || s.MemberId == uid)),
            TimeRequest r => r.CoachId == uid || r.MemberId == uid || (r.MemberId is null && CoachesMember(r.CoachId, uid)),
            ChatMessage c => c.MemberId == uid || c.CoachId == uid,   // private: not even admins read chats
            ShiftPost s => s.CoachId == uid || staff || CoachesMember(s.CoachId, uid),
            GymCheckIn c => c.MemberId == uid || staff,
            Discount => Has("discounts:manage"),   // members only learn a code is valid by using it
            MembershipPackage k => k.IsActive || Has("catalog:manage"),
            Amenity a => a.IsActive || Has("catalog:manage"),
            AmenityFollow f => f.MemberId == uid,
            SupportThread t => t.MemberId == uid || Has("support:reply"),
            SupportMessage m => m.MemberId == uid || Has("support:reply"),
            _ => true
        };
    }

    public Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException<IReadOnlyList<T>>(FailWith);
        IEnumerable<T> rows = All<T>().Where(r => Allowed(r!));
        if (where is not null) rows = rows.Where(where.Compile());
        return Task.FromResult<IReadOnlyList<T>>(rows.Select(Clone).ToList());
    }

    // Direct writes are limited to what the table privileges and policies allow (0006). Everything else is a function.
    const string Rls = "new row violates row-level security policy";

    public Task<T> InsertAsync<T>(T row) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException<T>(FailWith);
        try
        {
            RequireActive();
            switch (row)
            {
                case MembershipPackage or Amenity: if (!Has("catalog:manage")) throw Err(Rls); break;
                case Discount: if (!Has("discounts:manage")) throw Err(Rls); break;
                case ShiftPost s:
                    if (s.CoachId != Uid || !IsCoach(Uid)) throw Err(Rls);
                    if (All<ShiftPost>().Any(x => x.CoachId == s.CoachId && x.Status == Status.Scheduled && x.Start < s.End && s.Start < x.End)) throw Err("overlap");
                    break;
                case ChatMessage c:
                    if (c.SenderId != Uid || !CoachesMember(c.CoachId, c.MemberId) || !ActiveMember(c.MemberId)) throw Err(Rls);
                    // the table's own checks: the sender is one of the two people, there is text or a photo, and the photo is in this conversation's folder
                    var photoOk = c.AttachmentPath is null || System.Text.RegularExpressions.Regex.IsMatch(c.AttachmentPath, $"^{c.CoachId}/{c.MemberId}/{FilePart}$");
                    if (c.SenderId != c.CoachId && c.SenderId != c.MemberId || (string.IsNullOrWhiteSpace(c.Body) && c.AttachmentPath is null) || !photoOk)
                        throw Err("new row for relation \"messages\" violates check constraint (23514)");
                    break;
                case AmenityUsage u:
                    if (!ActiveMember(Uid) || !All<UserAmenity>().Any(a => a.Id == u.UserAmenityId && a.MemberId == Uid && a.Status == Status.Active && (a.ExpiresAt is null || a.ExpiresAt > DateTime.UtcNow)))
                        throw Err(Rls);
                    break;
                default: throw Err(Rls);
            }
        }
        catch (Exception ex) { return Task.FromException<T>(ex); }

        var added = Add(row);
        switch (added)
        {
            case ShiftPost s:
                s.Status = Status.Scheduled;
                foreach (var h in All<CoachHire>().Where(h => h.CoachId == s.CoachId && h.Status == Status.Active)) Notify(h.MemberId, "system", "Your coach posted a shift");
                break;
            case ChatMessage c:
                c.SentAt = DateTime.UtcNow; c.ReadAt = null; c.DeliveredAt = null;
                NotifyChat(c.SenderId == c.CoachId ? c.MemberId : c.CoachId, "New message", null, $"c|{c.CoachId}|{c.MemberId}");
                break;
            case AmenityUsage u: u.UsedAt = DateTime.UtcNow; u.Status = Status.Pending; break;
        }
        return Task.FromResult(Clone(added));
    }

    public Task UpdateAsync<T>(T row) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        try
        {
            RequireActive();
            var pk = Pk(typeof(T));
            var key = pk.GetValue(row);
            var list = Table(typeof(T));
            var index = list.FindIndex(r => Equals(pk.GetValue(r), key));
            if (index < 0 || !Allowed(list[index])) throw Err("not_found");

            switch (row, list[index])
            {
                case (MembershipPackage, _): if (!Has("catalog:manage")) throw Err(Rls); list[index] = Clone(row); break;
                case (Amenity a, Amenity old):       // a catalog edit never changes the live status, like the column rights in the database
                    if (!Has("catalog:manage")) throw Err(Rls);
                    var edited = Clone(a); edited.Availability = old.Availability; edited.StatusReason = old.StatusReason; edited.BackAt = old.BackAt;
                    list[index] = edited;
                    break;
                case (Discount, _): if (!Has("discounts:manage")) throw Err(Rls); list[index] = Clone(row); break;
                case (GymSettings, _): if (!Has("gym_settings:manage")) throw Err(Rls); list[index] = Clone(row); break;
                case (ShiftPost s, ShiftPost old):   // a coach changes times, note and status, never the owner
                    if (old.CoachId != Uid) throw Err(Rls);
                    old.Start = s.Start; old.End = s.End; old.Note = s.Note; old.Status = s.Status;
                    break;
                case (Coach c, Coach old):           // bio, specialty, rate, availability
                    if (old.UserId != Uid && !RolesOf(Uid).Contains("admin")) throw Err(Rls);
                    old.Bio = c.Bio; old.Specialty = c.Specialty; old.HourlyRate = c.HourlyRate; old.IsAvailable = c.IsAvailable;
                    break;
                case (UserSettings s, UserSettings old):
                    if (old.UserId != Uid) throw Err(Rls);
                    old.Theme = s.Theme; old.Language = s.Language; old.Timezone = s.Timezone;
                    break;
                case (UserNotification, UserNotification old):   // only read_at
                    if (old.UserId != Uid) throw Err(Rls);
                    old.ReadAt = ((UserNotification)(object)row).ReadAt;
                    break;
                case (Profile p, Profile old):
                    if (old.Id != Uid) throw Err(Rls);
                    old.FullName = p.FullName; old.Phone = p.Phone;
                    break;
                case (Member m, Member old):
                    if (old.UserId != Uid) throw Err(Rls);
                    old.Goals = m.Goals;
                    break;
                default: throw Err(Rls);
            }
            return Task.CompletedTask;
        }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    // The demo supports exactly what the app uses this for: marking your own notifications read. It follows the same ownership rule as UpdateAsync.
    public Task UpdateWhereAsync<T, TValue>(Expression<Func<T, bool>> where, Expression<Func<T, TValue>> column, TValue value) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        if (typeof(T) != typeof(UserNotification) || column.Body is not MemberExpression { Member.Name: nameof(UserNotification.ReadAt) })
            return Task.FromException(new NotSupportedException("the demo only sets read_at on notifications"));
        var matches = where.Compile();
        foreach (var n in All<UserNotification>().Where(n => n.UserId == Uid && matches((T)(object)n)).ToList())
            n.ReadAt = (DateTime?)(object?)value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(T row) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        try
        {
            RequireActive();
            const string fk = "violates foreign key constraint";   // what the database says when something still points at the row
            switch (row)
            {
                case MembershipPackage p:
                    if (!Has("catalog:manage")) throw Err(Rls);
                    if (All<UserMembershipPackage>().Any(m => m.MembershipPackageId == p.Id)) throw Err($"update or delete on membership_packages {fk}");
                    break;
                case Amenity a:
                    if (!Has("catalog:manage")) throw Err(Rls);
                    if (All<UserAmenity>().Any(x => x.AmenityId == a.Id)) throw Err($"update or delete on amenities {fk}");
                    break;
                case Discount d:
                    if (!Has("discounts:manage")) throw Err(Rls);
                    if (All<Payment>().Any(x => x.DiscountId == d.Id)) throw Err($"update or delete on discounts {fk}");
                    break;
                case ShiftPost s when s.CoachId == Uid: break;
                case UserNotification n when n.UserId == Uid: break;
                default: throw Err(Rls);
            }
            var pk = Pk(typeof(T));
            var key = pk.GetValue(row);
            Table(typeof(T)).RemoveAll(r => Equals(pk.GetValue(r), key));
            return Task.CompletedTask;
        }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    // ---------- chat photos (mirrors 0016: can_use_chat_file) ----------
    readonly Dictionary<string, byte[]> _files = new();
    const string FilePart = @"[A-Za-z0-9_-][A-Za-z0-9._-]{0,119}";
    static readonly System.Text.RegularExpressions.Regex SupportFile = new(@$"^support/(?<m>[A-Za-z0-9_-]+)/{FilePart}$");
    static readonly System.Text.RegularExpressions.Regex CoachFile = new(@$"^(?<c>[A-Za-z0-9_-]+)/(?<m>[A-Za-z0-9_-]+)/{FilePart}$");

    bool CanUseChatFile(string path, bool write)
    {
        var uid = CurrentUserId;
        if (uid is null || !IsActiveUser) return false;
        if (SupportFile.Match(path) is { Success: true } s) return s.Groups["m"].Value == uid || Has("support:reply");
        if (CoachFile.Match(path) is { Success: true } c)
        {
            var coach = c.Groups["c"].Value; var member = c.Groups["m"].Value;
            if (uid != coach && uid != member) return false;
            return !write || (CoachesMember(coach, member) && ActiveMember(member));
        }
        return false;
    }

    public Task<string> UploadAsync(string bucket, string path, byte[] data)
    {
        if (FailWith is not null) return Task.FromException<string>(FailWith);
        if (bucket != "chat-attachments") return Task.FromResult(path);
        path = StoragePath.Safe(path);
        if (!CanUseChatFile(path, write: true)) return Task.FromException<string>(Err(Rls));
        _files[path] = data;
        return Task.FromResult(path);
    }

    public Task<string?> SignedUrlAsync(string bucket, string path)
    {
        if (FailWith is not null) return Task.FromException<string?>(FailWith);
        if (bucket != "chat-attachments" || !CanUseChatFile(path, write: false) || !_files.TryGetValue(path, out var bytes)) return Task.FromResult<string?>(null);   // the demo keeps no other files
        // a plain file in the temp folder stands in for the signed link, so the picture shows without a server
        var file = Path.Combine(Path.GetTempPath(), $"gym-demo-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16]}{Path.GetExtension(path)}");
        if (!File.Exists(file)) File.WriteAllBytes(file, bytes);
        return Task.FromResult<string?>(file);
    }

    /// <summary>What the list shows for a message: its text, or the word Photo when it has none.</summary>
    static string? Preview(string? body, string? photo) => !string.IsNullOrWhiteSpace(body) ? body : photo is not null ? "Photo" : body;

    public Task RpcAsync(string function, Dictionary<string, object>? args = null)
    {
        try { if (FailWith is not null) throw FailWith; Call(function, args ?? new()); return Task.CompletedTask; }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    public Task<TResult> RpcAsync<TResult>(string function, Dictionary<string, object>? args = null)
    {
        try { if (FailWith is not null) throw FailWith; return Task.FromResult((TResult)Call(function, args ?? new())!); }
        catch (Exception ex) { return Task.FromException<TResult>(ex); }
    }

    // ---------- payments: helpers shared by the functions below (0005_functions.sql) ----------
    static string PaymentCode()
    {
        const string chars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return "GYM-" + new string(Enumerable.Range(0, 5).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    string ReceiptNo()
    {
        var prefix = All<GymSettings>().First().ReceiptPrefix;
        return $"{prefix}-{DateTime.UtcNow.AddHours(8).Year}-{++_receiptSeq:D6}";
    }

    /// <summary>No code: full price. A bad, expired or already-used code is an error, never a silent full price.</summary>
    (int? Id, decimal Cut, decimal Amount) Discounted(string member, Dictionary<string, object> a, decimal price)
    {
        if (!a.TryGetValue("p_discount_code", out var raw) || string.IsNullOrWhiteSpace(raw as string)) return (null, 0, price);
        var code = ((string)raw).Trim();
        var now = DateTime.UtcNow;
        var d = All<Discount>().FirstOrDefault(x => x.IsActive && x.StartsAt <= now && x.ExpiresAt > now && string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        if (d is null || All<Payment>().Any(p => p.MemberId == member && p.DiscountId == d.Id && p.Status is Status.Pending or Status.Verified)) throw Err("invalid_code");
        var cut = Math.Round(price * d.Percent / 100m, 2);
        return (d.Id, cut, price - cut);
    }

    Payment NewQrPayment(string kind, string member, (int? Id, decimal Cut, decimal Amount) d, int? ump = null, int? renewal = null, int? amenity = null)
    {
        var hours = All<GymSettings>().First().PaymentExpiryHours;
        return Add(new Payment
        {
            PaymentCode = PaymentCode(), Kind = kind, Method = PayMethod.Qr, Status = Status.Pending, Amount = d.Amount, DiscountId = d.Id, DiscountAmount = d.Cut,
            MemberId = member, UserMembershipPackageId = ump, RenewalId = renewal, UserAmenityId = amenity,
            ExpiresAt = DateTime.UtcNow.AddHours(hours), CreatedAt = DateTime.UtcNow
        });
    }

    // plan 8.4: switch on whatever the payment paid for
    void ActivateTarget(Payment pay)
    {
        var now = DateTime.UtcNow;
        if (pay.UserMembershipPackageId is { } umpId)
        {
            var ump = All<UserMembershipPackage>().First(m => m.Id == umpId);
            if (All<UserMembershipPackage>().Any(m => m.MemberId == ump.MemberId && m.Id != ump.Id && m.Status == Status.Active)) throw Err("duplicate key value violates unique constraint");
            var days = All<MembershipPackage>().First(p => p.Id == ump.MembershipPackageId).DurationDays;
            ump.Status = Status.Active; ump.StartsAt = now; ump.EndsAt = now.AddDays(days);
        }
        else if (pay.RenewalId is { } renId)
        {
            var ren = All<MembershipRenewal>().First(r => r.Id == renId);
            var ump = All<UserMembershipPackage>().First(m => m.Id == ren.UserMembershipPackageId);
            ump.Status = Status.Active; ump.EndsAt = ren.NewEndsAt;
            ren.Status = Status.Completed; ren.CompletedAt = now;
        }
        else if (pay.UserAmenityId is { } uaId)
        {
            var ua = All<UserAmenity>().First(a => a.Id == uaId);
            var days = All<Amenity>().First(a => a.Id == ua.AmenityId).DurationDays;
            ua.Status = Status.Active; ua.ExpiresAt = days is null ? null : now.AddDays(days.Value);
        }
    }

    // rejected or unpaid: the pending thing the payment was for is cancelled
    void CancelTarget(Payment pay)
    {
        foreach (var m in All<UserMembershipPackage>().Where(m => m.Id == pay.UserMembershipPackageId && m.Status == Status.Pending)) m.Status = Status.Cancelled;
        foreach (var r in All<MembershipRenewal>().Where(r => r.Id == pay.RenewalId && r.Status == Status.Pending)) r.Status = Status.Cancelled;
        foreach (var a in All<UserAmenity>().Where(a => a.Id == pay.UserAmenityId && a.Status == Status.Pending)) a.Status = Status.Cancelled;
    }

    bool Overlap(string who, bool coach, DateTime start, DateTime end, int ignore = 0) =>
        All<TrainingSession>().Any(s => s.Id != ignore && s.Status == Status.Scheduled && (coach ? s.CoachId : s.MemberId) == who && s.ScheduledStart < end && start < s.ScheduledEnd);

    // ---------- functions (mirror 0005_functions.sql) ----------
    object? Call(string fn, Dictionary<string, object> a)
    {
        var now = DateTime.UtcNow;
        int I(string k) => Convert.ToInt32(a[k]);
        bool B(string k) => Convert.ToBoolean(a[k]);
        string S(string k) => (string)a[k];
        string? Opt(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        DateTime T(string k) => DateTime.Parse(S(k), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
        var uid = Uid;

        switch (fn)
        {
            case "avail_membership":
            {
                RequireActive();
                if (All<Member>().All(m => m.UserId != uid)) throw Err("forbidden");
                var pkg = All<MembershipPackage>().FirstOrDefault(p => p.Id == I("p_package_id") && p.IsActive) ?? throw Err("not_found");
                if (All<UserMembershipPackage>().Any(m => m.MemberId == uid && m.Status is Status.Pending or Status.Active)) throw Err("already_availed");
                var d = Discounted(uid, a, pkg.Price);
                var ump = Add(new UserMembershipPackage { MemberId = uid, MembershipPackageId = pkg.Id, Status = Status.Pending });
                return Clone(NewQrPayment(PayKind.Membership, uid, d, ump: ump.Id));
            }
            case "avail_amenity":
            {
                RequireActive();
                if (!ActiveMember(uid)) throw Err("no_active_membership");
                var am = All<Amenity>().FirstOrDefault(x => x.Id == I("p_amenity_id") && x.IsActive) ?? throw Err("not_found");
                if (All<UserAmenity>().Any(x => x.MemberId == uid && x.AmenityId == am.Id && x.Status is Status.Pending or Status.Active)) throw Err("already_availed");
                var d = Discounted(uid, a, am.Price);
                var ua = Add(new UserAmenity { MemberId = uid, AmenityId = am.Id, Status = Status.Pending, AvailedAt = now });
                return Clone(NewQrPayment(PayKind.Amenity, uid, d, amenity: ua.Id));
            }
            case "renew_membership":
            {
                RequireActive();
                var ump = All<UserMembershipPackage>().FirstOrDefault(m => m.Id == I("p_ump_id") && m.MemberId == uid) ?? throw Err("not_found");
                if (ump.Status is not (Status.Active or Status.Expired)) throw Err("not_renewable");
                if (All<MembershipRenewal>().Any(r => r.UserMembershipPackageId == ump.Id && r.Status == Status.Pending)) throw Err("renewal_pending");
                var pkg = All<MembershipPackage>().First(p => p.Id == ump.MembershipPackageId);
                var d = Discounted(uid, a, pkg.Price);
                var start = ump.EndsAt is { } e && e > now ? e : now;   // an expired term renews from today, not from the past end date
                var ren = Add(new MembershipRenewal { UserMembershipPackageId = ump.Id, PreviousEndsAt = ump.EndsAt ?? now, NewStartsAt = start, NewEndsAt = start.AddDays(pkg.DurationDays) });
                return Clone(NewQrPayment(PayKind.Renewal, uid, d, renewal: ren.Id));
            }
            case "submit_payment_proof":
            {
                RequireActive();
                var pay = All<Payment>().FirstOrDefault(p => p.Id == I("p_payment_id") && p.MemberId == uid) ?? throw Err("not_found");
                var reference = Opt("p_reference_no"); var proof = Opt("p_proof_path");
                if (reference is null || proof is null) throw Err("bad_input");
                if (pay.Status != Status.Pending || pay.Method != PayMethod.Qr || pay.ExpiresAt <= now) throw Err("already_processed");
                if (All<Payment>().Any(p => p.Id != pay.Id && p.ReferenceNo == reference && p.Status is Status.Pending or Status.Verified))
                    throw Err("duplicate key value violates unique constraint \"payments_reference_no_key\"");
                pay.ReferenceNo = reference; pay.ProofPath = proof;
                NotifyStaff("payment", "Payment waiting for review");
                return null;
            }
            case "verify_payment":
            {
                Require("payments:verify");
                var pay = All<Payment>().FirstOrDefault(p => p.Id == I("p_payment_id")) ?? throw Err("not_found");
                if (pay.Status != Status.Pending) throw Err("already_processed");
                if (pay.MemberId == uid) throw Err("self_verify");
                if (B("p_approve"))
                {
                    pay.Status = Status.Verified; pay.VerifiedBy = uid; pay.VerifiedAt = now; pay.ReceiptNo = ReceiptNo();
                    ActivateTarget(pay);
                    Notify(pay.MemberId, "payment", "Payment approved", pay.PaymentCode);
                    Audit("payment.verify", "payments", pay.Id.ToString());
                }
                else
                {
                    var reason = Opt("p_reason") ?? throw Err("bad_input");
                    pay.Status = Status.Rejected; pay.RejectedReason = reason;
                    CancelTarget(pay);
                    Notify(pay.MemberId, "payment", "Payment rejected", reason);
                    Audit("payment.reject", "payments", pay.Id.ToString());
                }
                return null;
            }
            case "record_counter_payment":
            {
                Require("payments:record");
                var kind = S("p_kind"); var method = S("p_method"); var memberId = Opt("p_member_id");
                if (method == PayMethod.Qr) throw Err("bad_input");
                if (memberId == uid) throw Err("self_verify");
                var reference = Opt("p_reference_no");
                if (method is PayMethod.Gcash or PayMethod.BankTransfer && reference is null) throw Err("bad_input");
                var itemId = a.ContainsKey("p_item_id") ? I("p_item_id") : 0;
                var d = (Id: (int?)null, Cut: 0m, Amount: 0m);
                int? ump = null, ren = null, ua = null;
                string? walkIn = null;

                if (kind == PayKind.WalkIn)
                {
                    walkIn = Opt("p_walk_in_name") ?? throw Err("bad_input");
                    memberId = null;
                    d = (null, 0, All<GymSettings>().First().WalkInPrice);
                }
                else
                {
                    if (memberId is null || All<Member>().All(m => m.UserId != memberId)) throw Err("not_found");
                    if (kind == PayKind.Membership)
                    {
                        var pkg = All<MembershipPackage>().FirstOrDefault(p => p.Id == itemId && p.IsActive) ?? throw Err("not_found");
                        if (All<UserMembershipPackage>().Any(m => m.MemberId == memberId && m.Status is Status.Pending or Status.Active)) throw Err("already_availed");
                        d = Discounted(memberId, a, pkg.Price);
                        ump = Add(new UserMembershipPackage { MemberId = memberId, MembershipPackageId = pkg.Id, Status = Status.Pending }).Id;
                    }
                    else if (kind == PayKind.Renewal)
                    {
                        var cur = All<UserMembershipPackage>().FirstOrDefault(m => m.Id == itemId && m.MemberId == memberId) ?? throw Err("not_found");
                        if (cur.Status is not (Status.Active or Status.Expired)) throw Err("not_renewable");
                        if (All<MembershipRenewal>().Any(r => r.UserMembershipPackageId == cur.Id && r.Status == Status.Pending)) throw Err("renewal_pending");
                        var pkg = All<MembershipPackage>().First(p => p.Id == cur.MembershipPackageId);
                        d = Discounted(memberId, a, pkg.Price);
                        var start = cur.EndsAt is { } e && e > now ? e : now;
                        ren = Add(new MembershipRenewal { UserMembershipPackageId = cur.Id, PreviousEndsAt = cur.EndsAt ?? now, NewStartsAt = start, NewEndsAt = start.AddDays(pkg.DurationDays) }).Id;
                    }
                    else
                    {
                        if (!ActiveMember(memberId)) throw Err("no_active_membership");
                        var am = All<Amenity>().FirstOrDefault(x => x.Id == itemId && x.IsActive) ?? throw Err("not_found");
                        if (All<UserAmenity>().Any(x => x.MemberId == memberId && x.AmenityId == am.Id && x.Status is Status.Pending or Status.Active)) throw Err("already_availed");
                        d = Discounted(memberId, a, am.Price);
                        ua = Add(new UserAmenity { MemberId = memberId, AmenityId = am.Id, Status = Status.Pending, AvailedAt = now }).Id;
                    }
                }

                var pay = Add(new Payment
                {
                    PaymentCode = PaymentCode(), Kind = kind, Method = method, Status = Status.Verified, Amount = d.Amount, DiscountId = d.Id, DiscountAmount = d.Cut,
                    MemberId = memberId, WalkInName = walkIn, UserMembershipPackageId = ump, RenewalId = ren, UserAmenityId = ua, ReferenceNo = reference,
                    RecordedBy = uid, VerifiedBy = uid, VerifiedAt = now, ReceiptNo = ReceiptNo(), CreatedAt = now
                });
                if (kind == PayKind.WalkIn) Add(new GymCheckIn { PaymentId = pay.Id, CheckedInAt = now });
                else { ActivateTarget(pay); Notify(memberId, "payment", "Payment received", pay.PaymentCode); }
                Audit("payment.record", "payments", pay.Id.ToString());
                return Clone(pay);
            }
            case "void_payment":
            {
                Require("payments:void");
                var reason = Opt("p_reason") ?? throw Err("bad_input");
                var pay = All<Payment>().FirstOrDefault(p => p.Id == I("p_payment_id")) ?? throw Err("not_found");
                if (pay.Status != Status.Verified) throw Err("already_processed");
                pay.Status = Status.Voided; pay.VoidedBy = uid; pay.VoidedAt = now; pay.VoidReason = reason;
                if (pay.UserMembershipPackageId is { } umpId) All<UserMembershipPackage>().First(m => m.Id == umpId).Status = Status.Cancelled;
                else if (pay.RenewalId is { } renId)
                {
                    var ren = All<MembershipRenewal>().First(r => r.Id == renId);
                    var ump = All<UserMembershipPackage>().First(m => m.Id == ren.UserMembershipPackageId);
                    ump.EndsAt = ren.PreviousEndsAt; ump.Status = ren.PreviousEndsAt > now ? Status.Active : Status.Expired;
                    ren.Status = Status.Cancelled;
                }
                else if (pay.UserAmenityId is { } uaId) All<UserAmenity>().First(x => x.Id == uaId).Status = Status.Cancelled;
                else Table(typeof(GymCheckIn)).RemoveAll(c => ((GymCheckIn)c).PaymentId == pay.Id);
                Notify(pay.MemberId, "payment", "Payment voided", reason);
                Audit("payment.void", "payments", pay.Id.ToString());
                return null;
            }
            case "revenue_summary":
            {
                Require("reports:revenue");
                var from = DateTime.Parse(S("p_from")).Date; var to = DateTime.Parse(S("p_to")).Date;
                DateTime Day(Payment p) => p.VerifiedAt!.Value.AddHours(8).Date;   // Manila
                return All<Payment>().Where(p => p.Status == Status.Verified && Day(p) >= from && Day(p) <= to)
                    .GroupBy(p => (Day: Day(p), p.Kind, p.Method)).OrderBy(g => g.Key.Day).ThenBy(g => g.Key.Kind).ThenBy(g => g.Key.Method)
                    .Select(g => new RevenueRow(g.Key.Day, g.Key.Kind, g.Key.Method, g.Count(), g.Sum(p => p.Amount))).ToList();
            }
            case "daily_cash_summary":
            {
                Require("reports:cash");
                var day = DateTime.Parse(S("p_day")).Date;
                return All<Payment>().Where(p => p.Status == Status.Verified && p.Method == PayMethod.Cash && p.VerifiedAt!.Value.AddHours(8).Date == day)
                    .GroupBy(p => p.Kind).OrderBy(g => g.Key).Select(g => new CashRow(g.Key, g.Count(), g.Sum(p => p.Amount))).ToList();
            }

            // ----- coaching -----
            case "hire_coach":
            {
                RequireActive();
                if (!ActiveMember(uid)) throw Err("no_active_membership");
                var coachId = S("p_coach_id");
                if (!All<Coach>().Any(c => c.UserId == coachId && c.IsAvailable) || !(All<Profile>().FirstOrDefault(p => p.Id == coachId)?.IsActive ?? false)) throw Err("not_found");
                if (CoachesMember(coachId, uid)) throw Err("duplicate key value violates unique constraint");
                var hire = Add(new CoachHire { MemberId = uid, CoachId = coachId, Status = Status.Active, HiredAt = now });
                Notify(coachId, "system", "New trainee", All<Profile>().First(p => p.Id == uid).FullName);
                return hire.Id;
            }
            case "end_hire":
            {
                RequireActive();
                var h = All<CoachHire>().FirstOrDefault(x => x.Id == I("p_hire_id") && (x.MemberId == uid || x.CoachId == uid)) ?? throw Err("not_found");
                if (h.Status != Status.Active) throw Err("already_processed");
                h.Status = Status.Ended; h.EndedAt = now;
                foreach (var s in All<TrainingSession>().Where(s => s.CoachId == h.CoachId && s.MemberId == h.MemberId && s.Status == Status.Scheduled && s.ScheduledStart > now)) s.Status = Status.Cancelled;
                foreach (var r in All<TimeRequest>().Where(r => r.CoachId == h.CoachId && r.MemberId == h.MemberId && r.Status == Status.Pending)) r.Status = Status.Cancelled;
                Notify(uid == h.CoachId ? h.MemberId : h.CoachId, "system", "Coaching ended");
                return null;
            }
            case "request_time":
            {
                RequireActive();
                var coachId = S("p_coach_id"); var start = T("p_start"); var end = T("p_end");
                if (end <= start) throw Err("end must be after start");
                TimeRequest req;
                if (uid == coachId)
                {
                    if (!IsCoach(uid)) throw Err("forbidden");
                    req = Add(new TimeRequest { CoachId = uid, MemberId = null, RequestedBy = "coach", RequestedStart = start, RequestedEnd = end, Message = Opt("p_message") });
                    foreach (var h in All<CoachHire>().Where(h => h.CoachId == uid && h.Status == Status.Active)) Notify(h.MemberId, "time_request", "Your coach offered an open slot");
                }
                else
                {
                    if (!ActiveMember(uid)) throw Err("no_active_membership");
                    if (!CoachesMember(coachId, uid)) throw Err("no_active_hire");
                    req = Add(new TimeRequest { CoachId = coachId, MemberId = uid, RequestedBy = "member", RequestedStart = start, RequestedEnd = end, Message = Opt("p_message") });
                    Notify(coachId, "time_request", "New time request");
                }
                return req.Id;
            }
            case "request_reschedule":
            {
                RequireActive();
                var start = T("p_start"); var end = T("p_end");
                if (end <= start) throw Err("end must be after start");
                var s = All<TrainingSession>().FirstOrDefault(x => x.Id == I("p_session_id") && (x.CoachId == uid || x.MemberId == uid)) ?? throw Err("not_found");
                if (s.Status != Status.Scheduled || s.ScheduledStart <= now || All<TimeRequest>().Any(r => r.SessionId == s.Id && r.Status == Status.Pending)) throw Err("already_processed");
                var byCoach = uid == s.CoachId;
                var req = Add(new TimeRequest { CoachId = s.CoachId, MemberId = s.MemberId, SessionId = s.Id, RequestedBy = byCoach ? "coach" : "member",
                                                RequestedStart = start, RequestedEnd = end, Message = Opt("p_message") });
                Notify(byCoach ? s.MemberId : s.CoachId, "time_request", "Reschedule requested");
                return req.Id;
            }
            case "respond_time_request":
            {
                RequireActive();
                var r = All<TimeRequest>().FirstOrDefault(x => x.Id == I("p_request_id")) ?? throw Err("not_found");
                if (r.Status != Status.Pending) throw Err("already_processed");
                var approve = B("p_approve");
                string member;
                if (r.MemberId is null)
                {   // an open slot: a trainee of that coach claims it, which counts as approving
                    if (!(ActiveMember(uid) && CoachesMember(r.CoachId, uid))) throw Err("forbidden");
                    if (!approve) throw Err("bad_input");
                    member = uid;
                }
                else if (r.RequestedBy == "member") { if (uid != r.CoachId) throw Err("forbidden"); member = r.MemberId; }
                else { if (uid != r.MemberId) throw Err("forbidden"); member = r.MemberId; }

                if (approve)
                {
                    if (r.SessionId is null && !ActiveMember(member)) throw Err("no_active_membership");
                    var ignore = r.SessionId ?? 0;
                    if (Overlap(r.CoachId, true, r.RequestedStart, r.RequestedEnd, ignore) || Overlap(member, false, r.RequestedStart, r.RequestedEnd, ignore)) throw Err("overlap");
                    if (r.SessionId is { } sid)
                    {   // a reschedule moves the booked session instead of creating a second one
                        var moved = All<TrainingSession>().First(s => s.Id == sid);
                        if (moved.Status != Status.Scheduled) throw Err("already_processed");
                        moved.ScheduledStart = r.RequestedStart; moved.ScheduledEnd = r.RequestedEnd;
                    }
                    else Add(new TrainingSession { CoachId = r.CoachId, MemberId = member, Title = "Training session", ScheduledStart = r.RequestedStart, ScheduledEnd = r.RequestedEnd });
                    r.Status = Status.Approved; r.MemberId = member;
                }
                else r.Status = Status.Rejected;
                Notify(uid == r.CoachId ? member : r.CoachId, "time_request", approve ? "Time request approved" : "Time request declined");
                return null;
            }
            case "cancel_time_request":
            {
                RequireActive();
                var r = All<TimeRequest>().FirstOrDefault(x => x.Id == I("p_request_id")) ?? throw Err("not_found");
                var mine = (r.RequestedBy == "member" && r.MemberId == uid) || (r.RequestedBy == "coach" && r.CoachId == uid);
                if (!mine) throw Err("forbidden");
                if (r.Status != Status.Pending) throw Err("already_processed");
                r.Status = Status.Cancelled;
                return null;
            }
            case "cancel_session":
            {
                RequireActive();
                var s = All<TrainingSession>().FirstOrDefault(x => x.Id == I("p_session_id") && (x.CoachId == uid || x.MemberId == uid)) ?? throw Err("not_found");
                if (s.Status != Status.Scheduled) throw Err("already_processed");
                s.Status = Status.Cancelled;
                foreach (var req in All<TimeRequest>().Where(x => x.SessionId == s.Id && x.Status == Status.Pending)) req.Status = Status.Cancelled;
                Notify(uid == s.CoachId ? s.MemberId : s.CoachId, "system", "A training session was cancelled", Opt("p_reason"));
                return null;
            }
            case "record_session_attendance":
            {
                RequireActive();
                var s = All<TrainingSession>().FirstOrDefault(x => x.Id == I("p_session_id") && x.CoachId == uid) ?? throw Err("forbidden");
                if (s.Status == Status.Cancelled) throw Err("already_processed");
                var status = S("p_status");
                if (status is not ("present" or "late" or "absent" or "excused")) throw Err("bad_input");
                var existing = All<SessionAttendance>().FirstOrDefault(x => x.SessionId == s.Id);
                if (existing is null) Add(new SessionAttendance { SessionId = s.Id, Status = status, CheckedInAt = status is "present" or "late" ? now : null, Notes = Opt("p_notes") });
                else { existing.Status = status; existing.CheckedInAt = status is "present" or "late" ? now : null; existing.Notes = Opt("p_notes"); }
                s.Status = status == "absent" ? Status.NoShow : Status.Completed;
                Notify(s.MemberId, "attendance", "Attendance recorded", status);
                return null;
            }

            // ----- front desk and accounts -----
            case "record_check_in":
            {
                Require("check_ins:record");
                var memberId = S("p_member_id");
                if (All<Member>().All(m => m.UserId != memberId)) throw Err("not_found");
                if (!ActiveMember(memberId)) throw Err("no_active_membership");
                return Add(new GymCheckIn { MemberId = memberId, CheckedInAt = now }).Id;
            }
            case "verify_amenity_usage":
            {
                Require("amenity_usages:verify");
                var u = All<AmenityUsage>().FirstOrDefault(x => x.Id == I("p_usage_id")) ?? throw Err("not_found");
                if (u.Status != Status.Pending) throw Err("already_processed");
                u.Status = B("p_approve") ? Status.Verified : Status.Rejected;
                Notify(All<UserAmenity>().First(x => x.Id == u.UserAmenityId).MemberId, "system", B("p_approve") ? "Amenity visit confirmed" : "Amenity visit rejected");
                return null;
            }
            case "assign_role":
            {
                Require("roles:manage");
                var role = All<Role>().FirstOrDefault(r => r.Name == S("p_role")) ?? throw Err("bad_input");
                if (All<Profile>().All(p => p.Id != S("p_user"))) throw Err("not_found");
                if (!All<UserRole>().Any(u => u.UserId == S("p_user") && u.RoleId == role.RoleId)) Add(new UserRole { UserId = S("p_user"), RoleId = role.RoleId });
                if (role.Name == "coach" && All<Coach>().All(c => c.UserId != S("p_user"))) Add(new Coach { UserId = S("p_user") });
                if (role.Name == "member" && All<Member>().All(m => m.UserId != S("p_user"))) Add(new Member { UserId = S("p_user") });
                Audit("role.assign", "user_roles", S("p_user"));
                return null;
            }
            case "revoke_role":
            {
                Require("roles:manage");
                if (S("p_user") == uid && S("p_role") == "admin") throw Err("forbidden");
                var role = All<Role>().First(r => r.Name == S("p_role"));
                Table(typeof(UserRole)).RemoveAll(u => ((UserRole)u).UserId == S("p_user") && ((UserRole)u).RoleId == role.RoleId);
                Audit("role.revoke", "user_roles", S("p_user"));
                return null;
            }
            case "set_user_active":
            {
                Require("users:manage");
                if (S("p_user") == uid) throw Err("forbidden");
                (All<Profile>().FirstOrDefault(p => p.Id == S("p_user")) ?? throw Err("not_found")).IsActive = B("p_active");
                Audit(B("p_active") ? "user.activate" : "user.deactivate", "profiles", S("p_user"));
                return null;
            }
            case "set_amenity_followed":
            {
                if (!All<Member>().Any(m => m.UserId == uid)) throw Err(Rls);
                var id = I("p_amenity_id");
                var saved = All<AmenityFollow>().FirstOrDefault(f => f.MemberId == uid && f.AmenityId == id);
                if (B("p_follow"))
                {
                    if (All<Amenity>().All(x => x.Id != id || !x.IsActive)) throw Err("not_found");
                    if (saved is null) Add(new AmenityFollow { MemberId = uid, AmenityId = id });
                }
                else if (saved is not null) Table(typeof(AmenityFollow)).Remove(saved);
                return null;
            }
            case "set_amenity_status":
            {
                Require("amenities:set_status");
                var amenity = All<Amenity>().FirstOrDefault(x => x.Id == I("p_amenity_id")) ?? throw Err("not_found");
                var status = S("p_status");
                var unavailable = status == "unavailable";
                var reason = unavailable ? Opt("p_reason") : null;
                DateTime? backAt = unavailable && Opt("p_back_at") is { } raw ? DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() : null;
                if (status is not ("available" or "in_use" or "unavailable")) throw Err("bad_status");
                if (reason?.Length > 200) throw Err("bad_reason");
                if (backAt <= DateTime.UtcNow) throw Err("bad_back_at");

                var told = new List<NotifiedMember>();
                if (amenity.Availability == status && amenity.StatusReason == reason && amenity.BackAt == backAt) return told;   // nothing changed: nobody is told
                amenity.Availability = status; amenity.StatusReason = reason; amenity.BackAt = backAt;
                Audit("amenity.status", "amenities", amenity.Id.ToString());
                if (status == "in_use") return told;

                var title = status == "available" ? $"{amenity.Name} is available" : $"{amenity.Name} is unavailable";
                var body = status == "available" ? null : string.Join(". ", new[] { reason, backAt is { } b ? $"Back about {b.AddHours(8):ddd dd MMM, h:mm tt}" : null }.Where(x => x is not null));
                foreach (var f in All<AmenityFollow>().Where(f => f.AmenityId == amenity.Id).ToList())
                {
                    var person = All<Profile>().FirstOrDefault(p => p.Id == f.MemberId);
                    if (person is not { IsActive: true }) continue;
                    Notify(f.MemberId, "amenity", title, body);
                    told.Add(new NotifiedMember(f.MemberId, person.FullName));
                }
                return told.OrderBy(t => t.Name).ToList();
            }
            // ---- messenger (mirrors 0015_messenger.sql)
            case "chat_threads":
            {
                RequireActive();
                string NameOf(string id) => All<Profile>().FirstOrDefault(p => p.Id == id)?.FullName ?? "Unknown";
                var rows = new List<ChatThread>();

                // coach chats: every hire (current or ended) and every pair already written to
                var pairs = All<CoachHire>().Select(h => (h.CoachId, h.MemberId)).Union(All<ChatMessage>().Select(m => (m.CoachId, m.MemberId)))
                    .Where(p => p.CoachId == uid || p.MemberId == uid).ToList();
                foreach (var (coachId, memberId) in pairs)
                {
                    var withId = coachId == uid ? memberId : coachId;
                    var msgs = All<ChatMessage>().Where(m => m.CoachId == coachId && m.MemberId == memberId).OrderBy(m => m.Id).ToList();
                    var last = msgs.LastOrDefault();
                    rows.Add(new ChatThread
                    {
                        Kind = "coach", CoachId = coachId, MemberId = memberId, WithId = withId, WithName = NameOf(withId),
                        WithAvatar = All<Profile>().FirstOrDefault(p => p.Id == withId)?.AvatarPath,
                        LastBody = last is null ? null : Preview(last.Body, last.AttachmentPath), LastAt = last?.SentAt, LastFromMe = last?.SenderId == uid,
                        Unread = msgs.Count(m => m.SenderId != uid && m.ReadAt is null),
                        CanSend = CoachesMember(coachId, memberId) && ActiveMember(memberId)
                    });
                }

                // a member's own front desk thread is always there
                if (All<Member>().Any(m => m.UserId == uid))
                {
                    var msgs = All<SupportMessage>().Where(m => m.MemberId == uid).OrderBy(m => m.Id).ToList();
                    var last = msgs.LastOrDefault();
                    rows.Add(new ChatThread
                    {
                        Kind = "support", MemberId = uid, WithName = "Front desk", LastBody = last is null ? null : Preview(last.Body, last.AttachmentPath), LastAt = last?.SentAt, LastFromMe = last?.SenderId == uid,
                        Unread = msgs.Count(m => m.SenderId != uid && m.ReadAt is null), CanSend = true,
                        Status = All<SupportThread>().FirstOrDefault(t => t.MemberId == uid)?.Status ?? "open"
                    });
                }

                // the inbox, for staff who may answer
                if (Has("support:reply"))
                    foreach (var t in All<SupportThread>().Where(t => t.MemberId != uid).OrderByDescending(t => t.LastMessageAt).Take(500))
                    {
                        var msgs = All<SupportMessage>().Where(m => m.MemberId == t.MemberId).OrderBy(m => m.Id).ToList();
                        var last = msgs.LastOrDefault();
                        rows.Add(new ChatThread
                        {
                            Kind = "support", MemberId = t.MemberId, WithId = t.MemberId, WithName = NameOf(t.MemberId),
                            WithAvatar = All<Profile>().FirstOrDefault(p => p.Id == t.MemberId)?.AvatarPath,
                            LastBody = last is null ? null : Preview(last.Body, last.AttachmentPath), LastAt = last?.SentAt, LastFromMe = last is not null && last.SenderId != t.MemberId,
                            Unread = msgs.Count(m => m.SenderId == t.MemberId && m.ReadAt is null), CanSend = true,
                            Status = t.Status, ClaimedBy = t.ClaimedBy, ClaimedByName = t.ClaimedBy is null ? null : NameOf(t.ClaimedBy)
                        });
                    }
                return rows.OrderByDescending(r => r.LastAt ?? DateTime.MinValue).ToList();
            }
            case "chat_messages":
            {
                RequireActive();
                var kind = S("p_kind"); var member = Opt("p_member"); var coach = Opt("p_coach");
                long? before = a.TryGetValue("p_before", out var bv) && bv is not null ? Convert.ToInt64(bv) : null;
                var limit = Math.Clamp(a.TryGetValue("p_limit", out var lv) && lv is not null ? Convert.ToInt32(lv) : 50, 1, 100);
                if (kind == "coach")
                {
                    if (uid != coach && uid != member) throw Err("forbidden");
                    return All<ChatMessage>().Where(m => m.CoachId == coach && m.MemberId == member && (before is null || m.Id < before)).OrderByDescending(m => m.Id).Take(limit)
                        .Select(m => new ChatLine { MessageId = m.Id, SenderId = m.SenderId, Body = m.Body, SentAt = m.SentAt, DeliveredAt = m.DeliveredAt, ReadAt = m.ReadAt, AttachmentPath = m.AttachmentPath }).ToList();
                }
                if (kind == "support")
                {
                    if (uid != member && !Has("support:reply")) throw Err("forbidden");
                    return All<SupportMessage>().Where(m => m.MemberId == member && (before is null || m.Id < before)).OrderByDescending(m => m.Id).Take(limit)
                        .Select(m => new ChatLine
                        {
                            MessageId = m.Id, SenderId = m.SenderId, Body = m.Body, SentAt = m.SentAt, DeliveredAt = m.DeliveredAt, ReadAt = m.ReadAt, AttachmentPath = m.AttachmentPath,
                            // a member never learns which staff member answered; staff do
                            SenderName = uid == member || m.SenderId == member ? null : All<Profile>().FirstOrDefault(p => p.Id == m.SenderId)?.FullName
                        }).ToList();
                }
                throw Err("bad_input");
            }
            case "mark_chat_read":
            {
                RequireActive();
                var kind = S("p_kind"); var member = Opt("p_member"); var coach = Opt("p_coach");
                if (kind == "coach")
                {
                    if (uid != coach && uid != member) throw Err("forbidden");
                    foreach (var m in All<ChatMessage>().Where(m => m.CoachId == coach && m.MemberId == member && m.SenderId != uid && m.ReadAt is null)) { m.DeliveredAt ??= now; m.ReadAt = now; }
                    ClearChatAlerts(uid, $"c|{coach}|{member}");
                }
                else if (kind == "support")
                {
                    if (uid != member && !Has("support:reply")) throw Err("forbidden");
                    foreach (var m in All<SupportMessage>().Where(m => m.MemberId == member && m.ReadAt is null && (uid == member ? m.SenderId != member : m.SenderId == member))) { m.DeliveredAt ??= now; m.ReadAt = now; }
                    ClearChatAlerts(uid, $"s|{member}");
                }
                else throw Err("bad_input");
                return null;
            }
            case "mark_chats_delivered":
            {
                RequireActive();
                foreach (var m in All<ChatMessage>().Where(m => (m.CoachId == uid || m.MemberId == uid) && m.SenderId != uid && m.DeliveredAt is null)) m.DeliveredAt = now;
                foreach (var m in All<SupportMessage>().Where(m => m.DeliveredAt is null && m.SenderId != uid && (m.MemberId == uid || (Has("support:reply") && m.SenderId == m.MemberId)))) m.DeliveredAt = now;
                return null;
            }
            case "send_support_message":
            {
                RequireActive();
                var member = S("p_member");
                var body = (a["p_body"] as string)?.Trim() ?? "";
                var att = Opt("p_attachment");
                if (body == "" && att is null) throw Err("empty_message");
                if (body.Length > 2000) throw Err("message_too_long");
                if (uid == member) { if (!All<Member>().Any(m => m.UserId == uid)) throw Err("forbidden"); }
                else if (Has("support:reply")) { if (!All<Member>().Any(m => m.UserId == member)) throw Err("not_found"); }
                else throw Err("forbidden");
                // the photo has to be in this member's front desk folder
                if (att is not null && !System.Text.RegularExpressions.Regex.IsMatch(att, $"^support/{member}/{FilePart}$")) throw Err("forbidden");

                var thread = All<SupportThread>().FirstOrDefault(t => t.MemberId == member) ?? Add(new SupportThread { MemberId = member });
                var msg = Add(new SupportMessage { MemberId = member, SenderId = uid, Body = body, SentAt = now, AttachmentPath = att });
                thread.LastMessageAt = now;
                var shown = Preview(body, att) ?? "";
                var key = $"s|{member}"; var preview = shown.Length > 80 ? shown[..80] : shown;
                if (uid == member)
                {
                    thread.Status = "open";   // writing again reopens it
                    var title = $"New message from {All<Profile>().First(p => p.Id == uid).FullName}";
                    if (thread.ClaimedBy is { } taker && All<Profile>().Any(p => p.Id == taker && p.IsActive)) NotifyChat(taker, title, preview, key);
                    else foreach (var id in All<UserRole>().Where(u => RolesOf(u.UserId).Any(r => r is "admin" or "employee")).Select(u => u.UserId).Distinct().ToList())
                        if (All<Profile>().Any(p => p.Id == id && p.IsActive)) NotifyChat(id, title, preview, key);
                }
                else NotifyChat(member, "Message from the front desk", preview, key);
                return (long)msg.Id;
            }
            case "claim_support_thread":
            {
                Require("support:reply");
                var thread = All<SupportThread>().FirstOrDefault(t => t.MemberId == S("p_member")) ?? throw Err("not_found");
                if (B("p_claim"))
                {
                    if (thread.ClaimedBy is not null && thread.ClaimedBy != uid && All<Profile>().Any(p => p.Id == thread.ClaimedBy && p.IsActive)) throw Err("already_processed");
                    thread.ClaimedBy = uid;
                }
                else
                {
                    if (thread.ClaimedBy is not null && thread.ClaimedBy != uid && !RolesOf(uid).Contains("admin") && All<Profile>().Any(p => p.Id == thread.ClaimedBy && p.IsActive)) throw Err("forbidden");
                    thread.ClaimedBy = null;
                }
                return null;
            }
            case "set_support_status":
            {
                Require("support:reply");
                (All<SupportThread>().FirstOrDefault(t => t.MemberId == S("p_member")) ?? throw Err("not_found")).Status = B("p_open") ? "open" : "closed";
                return null;
            }
            case "chat_search":
            {
                RequireActive();
                var q = ((a.TryGetValue("p_query", out var qv) ? qv as string : null) ?? "").Trim();
                var hits = new List<ChatSearchHit>();
                if (q.Length < 2) return hits;
                bool Has_(string? s) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;   // % and _ are plain characters here, like in the database
                foreach (var t in (List<ChatThread>)Call("chat_threads", new())!)
                {
                    var found = t.IsSupport
                        ? All<SupportMessage>().Where(m => m.MemberId == t.MemberId && Has_(m.Body)).OrderByDescending(m => m.SentAt).Select(m => (m.Body, m.SentAt)).FirstOrDefault()
                        : All<ChatMessage>().Where(m => m.CoachId == t.CoachId && m.MemberId == t.MemberId && Has_(m.Body)).OrderByDescending(m => m.SentAt).Select(m => (m.Body, m.SentAt)).FirstOrDefault();
                    if (found.Body is null && !Has_(t.WithName)) continue;
                    hits.Add(new ChatSearchHit
                    {
                        Kind = t.Kind, CoachId = t.CoachId, MemberId = t.MemberId, WithId = t.WithId, WithName = t.WithName,
                        Snippet = found.Body ?? t.LastBody, SentAt = found.Body is null ? t.LastAt : found.SentAt
                    });
                }
                return hits.OrderByDescending(h => h.SentAt ?? DateTime.MinValue).Take(30).ToList();
            }
            case "set_my_avatar":
            {
                var path = Opt("p_path");
                if (path is not null && !path.StartsWith(uid + "/")) throw Err("forbidden");
                (All<Profile>().FirstOrDefault(x => x.Id == uid) ?? throw Err("not_found")).AvatarPath = path;
                return null;
            }
            case "touch_last_seen": return null;   // the demo has no one to show it to
            case "mark_password_changed":
            {
                var p = All<Profile>().FirstOrDefault(x => x.Id == uid);
                if (p is not null) p.MustChangePassword = false;
                return null;
            }
            default: throw Err("not_found");
        }
    }

    // ---------- seed ----------
    string AddUser(string id, string name, string email, string role)
    {
        _accounts[email] = id;
        Add(new Profile { Id = id, Username = email.ToLowerInvariant(), FullName = name });
        Add(new UserSettings { UserId = id });
        Add(new UserRole { UserId = id, RoleId = All<Role>().First(x => x.Name == role).RoleId });
        if (role == "member") Add(new Member { UserId = id });
        if (role == "coach") Add(new Coach { UserId = id });
        return id;
    }

    void Seed()
    {
        var now = DateTime.UtcNow;
        DateTime At(int days, double hour) => DateTime.Today.AddDays(days).AddHours(hour).ToUniversalTime(); // seed in the viewer's local time
        foreach (var r in new[] { "admin", "employee", "coach", "member" }) Add(new Role { Name = r });
        Add(new GymSettings { GymName = "Gym Membership", QrAccountName = "Demo Gym (GCash)", WalkInPrice = 150, PaymentExpiryHours = 48 });

        AddUser("u-admin", "Dana Reyes", "admin@demo.io", "admin");
        AddUser("u-emp", "Marco Santos", "employee@demo.io", "employee");
        AddUser("u-coach1", "Lena Cruz", "coach@demo.io", "coach");
        AddUser("u-coach2", "Kai Mendoza", "kai@demo.io", "coach");
        AddUser("u-alex", "Alex Tan", "member@demo.io", "member");
        AddUser("u-bea", "Bea Lim", "bea@demo.io", "member");
        AddUser("u-carlo", "Carlo Diaz", "carlo@demo.io", "member");

        var lena = All<Coach>().First(c => c.UserId == "u-coach1"); lena.Specialty = "Strength and conditioning"; lena.HourlyRate = 800;
        lena.Bio = "Ten years coaching powerlifting and general strength. Works with beginners and returning lifters.";
        var kai = All<Coach>().First(c => c.UserId == "u-coach2"); kai.Specialty = "Boxing and mobility"; kai.HourlyRate = 700;
        kai.Bio = "Former amateur boxer. Sessions focus on footwork, conditioning and joint mobility.";

        var monthly = Add(new MembershipPackage { Name = "Monthly", Description = "Full gym floor access for 30 days.", Price = 1500, DurationDays = 30 });
        var quarterly = Add(new MembershipPackage { Name = "Quarterly", Description = "90 days of access at a lower monthly rate.", Price = 4000, DurationDays = 90 });
        Add(new MembershipPackage { Name = "Annual", Description = "A full year, best value for regulars.", Price = 14000, DurationDays = 365 });

        var pool = Add(new Amenity { Name = "Pool", Description = "25 m lap pool, open 6:00 to 21:00.", Price = 300, DurationDays = 30 });
        var sauna = Add(new Amenity { Name = "Sauna", Description = "Dry sauna next to the locker rooms.", Price = 200, DurationDays = 30,
                                      Availability = "unavailable", StatusReason = "Cleaning", BackAt = DateTime.UtcNow.AddMinutes(90) });
        Add(new Amenity { Name = "Group classes", Description = "Spin, yoga and circuit classes on the studio floor.", Price = 500, DurationDays = 30 });
        Add(new Amenity { Name = "Boxing ring", Description = "Closed for maintenance.", Price = 400, DurationDays = 30, IsActive = false });
        Add(new AmenityFollow { MemberId = "u-alex", AmenityId = pool.Id });     // Alex saved the pool and the sauna
        Add(new AmenityFollow { MemberId = "u-alex", AmenityId = sauna.Id });

        // Alex: active, 5 days left so the renew prompt shows
        var alexMembership = Add(new UserMembershipPackage { MemberId = "u-alex", MembershipPackageId = monthly.Id, Status = Status.Active, StartsAt = now.AddDays(-25), EndsAt = now.AddDays(5) });
        // Bea: paid by QR and sent the screenshot, waiting for the front desk
        var beaMembership = Add(new UserMembershipPackage { MemberId = "u-bea", MembershipPackageId = quarterly.Id, Status = Status.Pending });
        Add(new Payment { PaymentCode = "GYM-7F3K2", Kind = PayKind.Membership, Method = PayMethod.Qr, Amount = quarterly.Price, MemberId = "u-bea", UserMembershipPackageId = beaMembership.Id,
                          ReferenceNo = "GC-20260412-889", ProofPath = "u-bea/slip.jpg", ExpiresAt = now.AddHours(45), CreatedAt = now.AddHours(-3) });
        // Carlo: expired
        var carloMembership = Add(new UserMembershipPackage { MemberId = "u-carlo", MembershipPackageId = monthly.Id, Status = Status.Expired, StartsAt = now.AddDays(-42), EndsAt = now.AddDays(-12) });

        // verified history for the revenue screen: some by QR, some cash at the counter
        void Paid(string member, int umpId, decimal amount, int daysAgo, string method)
        {
            var when = now.AddDays(-daysAgo);
            Add(new Payment { PaymentCode = PaymentCode(), Kind = PayKind.Membership, Method = method, Status = Status.Verified, Amount = amount, MemberId = member, UserMembershipPackageId = umpId,
                              ExpiresAt = method == PayMethod.Qr ? when.AddHours(48) : null, RecordedBy = method == PayMethod.Qr ? null : "u-emp", VerifiedBy = "u-emp", VerifiedAt = when, ReceiptNo = ReceiptNo(), CreatedAt = when });
        }
        Paid("u-alex", alexMembership.Id, 1500, 25, PayMethod.Qr); Paid("u-carlo", carloMembership.Id, 1500, 42, PayMethod.Cash);
        Paid("u-alex", alexMembership.Id, 4000, 9, PayMethod.Qr); Paid("u-carlo", carloMembership.Id, 14000, 6, PayMethod.Cash);
        Paid("u-alex", alexMembership.Id, 1500, 3, PayMethod.Qr); Paid("u-carlo", carloMembership.Id, 4000, 1, PayMethod.Cash);
        Add(new Payment { PaymentCode = PaymentCode(), Kind = PayKind.WalkIn, Method = PayMethod.Cash, Status = Status.Verified, Amount = 150, WalkInName = "Juan Dela Cruz",
                          RecordedBy = "u-emp", VerifiedBy = "u-emp", VerifiedAt = now.AddDays(-2), ReceiptNo = ReceiptNo(), CreatedAt = now.AddDays(-2) });

        var alexPool = Add(new UserAmenity { MemberId = "u-alex", AmenityId = pool.Id, Status = Status.Active, AvailedAt = now.AddDays(-4), ExpiresAt = now.AddDays(26) });
        Add(new AmenityUsage { UserAmenityId = alexPool.Id, UsedAt = now.AddHours(-2), ProofPath = "u-alex/pool.jpg" });

        Add(new CoachHire { MemberId = "u-alex", CoachId = "u-coach1", HiredAt = now.AddDays(-20) });
        Add(new CoachHire { MemberId = "u-bea", CoachId = "u-coach1", HiredAt = now.AddDays(-2) });

        Add(new TimeRequest { CoachId = "u-coach1", MemberId = null, RequestedBy = "coach", RequestedStart = At(1, 10), RequestedEnd = At(1, 11), Message = "Open slot, strength session" });
        Add(new TimeRequest { CoachId = "u-coach1", MemberId = "u-alex", RequestedBy = "member", RequestedStart = At(2, 17), RequestedEnd = At(2, 18), Message = "Evening session after work?" });

        Add(new TrainingSession { CoachId = "u-coach1", MemberId = "u-alex", Title = "Squat technique", ScheduledStart = At(2, 9), ScheduledEnd = At(2, 10) });
        Add(new TrainingSession { CoachId = "u-coach1", MemberId = "u-alex", Title = "Upper body", ScheduledStart = At(-3, 9), ScheduledEnd = At(-3, 10), Status = Status.Completed });

        // a month of history for the coach's calendar: marks of every kind, a mixed day, a cancelled session and one more upcoming
        void Past(string member, int days, int hour, string status, string? mark)
        {
            var s = Add(new TrainingSession { CoachId = "u-coach1", MemberId = member, Title = "Training session", ScheduledStart = At(days, hour), ScheduledEnd = At(days, hour + 1), Status = status });
            if (mark is not null) Add(new SessionAttendance { SessionId = s.Id, Status = mark, CheckedInAt = mark is "present" or "late" ? At(days, hour) : null });
        }
        Past("u-alex", -1, 9, Status.Completed, "present");
        Past("u-bea", -2, 17, Status.Completed, "late");
        Past("u-alex", -5, 9, Status.NoShow, "absent");
        Past("u-bea", -6, 10, Status.Completed, "excused");
        Past("u-alex", -8, 9, Status.Completed, "present");        // a mixed day: attended and absent
        Past("u-bea", -8, 11, Status.NoShow, "absent");
        Past("u-alex", -12, 9, Status.Cancelled, null);
        Past("u-alex", -20, 9, Status.Completed, "present");
        Past("u-bea", -30, 9, Status.Completed, "present");
        Add(new TrainingSession { CoachId = "u-coach1", MemberId = "u-bea", Title = "Mobility", ScheduledStart = At(5, 9), ScheduledEnd = At(5, 10) });

        Add(new UserNotification { UserId = "u-alex", Type = "payment", Title = "Payment approved", CreatedAt = now.AddDays(-25), ReadAt = now.AddDays(-25) });
        Add(new UserNotification { UserId = "u-alex", Type = "membership", Title = "Membership expires in 5 days", Body = "Renew to keep your access.", CreatedAt = now.AddHours(-6) });
        Add(new UserNotification { UserId = "u-coach1", Type = "time_request", Title = "New time request", CreatedAt = now.AddHours(-1) });
        Add(new UserNotification { UserId = "u-emp", Type = "payment", Title = "Payment waiting for review", Body = "GYM-7F3K2", CreatedAt = now.AddHours(-3) });

        Add(new Discount { Code = "WELCOME10", Percent = 10, StartsAt = now.AddDays(-1), ExpiresAt = now.AddDays(60) });
        Add(new Discount { Code = "NEWYEAR20", Percent = 20, StartsAt = now.AddDays(-40), ExpiresAt = now.AddDays(-5) });

        Add(new ShiftPost { CoachId = "u-coach1", Start = At(1, 6), End = At(1, 14), Note = "Strength floor, squat rack priority" });
        Add(new ShiftPost { CoachId = "u-coach1", Start = At(3, 14), End = At(3, 20) });
        Add(new ShiftPost { CoachId = "u-coach2", Start = At(1, 14), End = At(1, 22), Note = "Heavy bags and pad work" });

        Add(new ChatMessage { CoachId = "u-coach1", MemberId = "u-alex", SenderId = "u-alex", Body = "Hi Lena, can we work on my squat depth this week?", SentAt = now.AddHours(-26), DeliveredAt = now.AddHours(-25.5), ReadAt = now.AddHours(-25.5) });
        Add(new ChatMessage { CoachId = "u-coach1", MemberId = "u-alex", SenderId = "u-coach1", Body = "Sure. Bring a flat-soled pair of shoes and we'll film it from the side.", SentAt = now.AddHours(-25) });
        Add(new ChatMessage { CoachId = "u-coach1", MemberId = "u-alex", SenderId = "u-alex", Body = "Perfect, see you on the session.", SentAt = now.AddHours(-24), DeliveredAt = now.AddHours(-23), ReadAt = now.AddHours(-23) });

        Add(new AuditEntry { ActorId = "u-emp", Action = "payment.verify", Entity = "payments", EntityId = "5", CreatedAt = now.AddDays(-3) });
        Add(new AuditEntry { ActorId = "u-admin", Action = "role.assign", Entity = "user_roles", EntityId = "u-coach2", CreatedAt = now.AddDays(-8) });
    }
}
