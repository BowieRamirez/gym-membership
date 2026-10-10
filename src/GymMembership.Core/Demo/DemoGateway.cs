using System.Linq.Expressions;
using System.Reflection;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Demo;

/// <summary>
/// In-memory stand-in for Supabase so the app can be explored without a database.
/// It mirrors the planned RLS visibility rules and RPC rules (see docs/specs/technical-spec.md §4–5).
/// Demo logins: admin@ / employee@ / coach@ / kai@ / member@ / bea@ / carlo@ demo.io, password "demo1234".
/// </summary>
public sealed class DemoGateway : ISupabaseGateway
{
    public const string DemoPassword = "demo1234";

    static readonly Dictionary<string, string[]> RolePermissions = new()
    {
        ["admin"] = ["users:manage", "packages:manage", "amenities:manage", "amenities:verify", "coaches:manage",
                     "payments:verify", "revenue:read", "audit:read", "checkins:record", "attendance:record"],
        ["employee"] = ["payments:verify", "amenities:verify", "checkins:record", "attendance:record"],
        ["coach"] = ["attendance:record"],
        ["member"] = []
    };

    readonly Dictionary<Type, List<object>> _tables = new();
    readonly Dictionary<string, string> _accounts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _passwords = new(); // only for accounts the admin created; everyone else uses DemoPassword
    readonly Dictionary<int, int> _renewalPayments = new();
    readonly Dictionary<(int Session, int Member), string> _attendance = new();
    readonly List<(int MemberId, DateTime At)> _checkIns = new();

    public string? CurrentUserId { get; private set; }
    public Exception? FailWith { get; set; }
    public IReadOnlyList<(int MemberId, DateTime At)> CheckIns => _checkIns;
    public IReadOnlyDictionary<(int Session, int Member), string> Attendance => _attendance;

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

    string Uid => CurrentUserId ?? throw new Exception("not authenticated");
    string[] MyRoles() => All<UserRole>().Where(u => u.UserId == CurrentUserId)
        .Select(u => All<Role>().First(r => r.RoleId == u.RoleId).Name).ToArray();
    bool Has(string permission) => MyRoles().Any(r => RolePermissions[r].Contains(permission));
    void Require(string permission) { if (!Has(permission)) throw new Exception("forbidden"); }
    int? MyMemberId => All<Member>().FirstOrDefault(m => m.UserId == CurrentUserId)?.Id;
    int? MyCoachId => All<Coach>().FirstOrDefault(c => c.UserId == CurrentUserId)?.Id;
    bool ActiveMembership(string userId) => All<UserMembershipPackage>()
        .Any(m => m.UserId == userId && m.Status == Status.Active && (m.EndsAt is null || m.EndsAt > DateTime.UtcNow));
    void Notify(string userId, string type, string title) =>
        Add(new UserNotification { UserId = userId, Type = type, Title = title, CreatedAt = DateTime.UtcNow });
    void Audit(string action, string entity, string entityId) =>
        Add(new AuditEntry { ActorId = CurrentUserId, Action = action, Entity = entity, EntityId = entityId, CreatedAt = DateTime.UtcNow });

    // ---------- auth ----------
    public Task SignInAsync(string email, string password)
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        if (!_accounts.TryGetValue(email.Trim(), out var uid) || password != _passwords.GetValueOrDefault(uid, DemoPassword))
            return Task.FromException(new Exception("Invalid login credentials"));
        if (!All<Profile>().First(p => p.Id == uid).IsActive)
            return Task.FromException(new Exception("forbidden"));
        CurrentUserId = uid;
        return Task.CompletedTask;
    }

    public Task SignUpAsync(string email, string password, string username)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(username) || !email.Contains('@') || password.Length < 6) return Task.FromException(new Exception("bad_input"));
        if (_accounts.ContainsKey(email)) return Task.FromException(new Exception("email_taken"));
        var id = AddUser("u-" + Guid.NewGuid().ToString("N")[..8], username.Trim(), email, "member");
        _passwords[id] = password;
        CurrentUserId = id;
        return Task.CompletedTask;
    }

    public Task SignOutAsync() { CurrentUserId = null; return Task.CompletedTask; }
    public Task<bool> RestoreSessionAsync() => Task.FromResult(false);

    // ---------- table access (RLS-shaped) ----------
    bool Allowed(object row)
    {
        var uid = CurrentUserId;
        var admin = Has("users:manage");
        var staff = Has("payments:verify");
        return row switch
        {
            Payment p => p.UserId == uid || staff || Has("revenue:read"),
            UserMembershipPackage m => m.UserId == uid || staff || admin,
            UserAmenity a => a.UserId == uid || Has("amenities:verify") || admin,
            AmenityUsage u => Has("amenities:verify") || All<UserAmenity>().Any(a => a.Id == u.UserAmenityId && a.UserId == uid),
            UserNotification n => n.UserId == uid,
            UserSettings s => s.UserId == uid || admin,
            UserRole r => r.UserId == uid || admin,
            AuditEntry => Has("audit:read"),
            CoachHire h => h.MemberId == MyMemberId || h.CoachId == MyCoachId || Has("coaches:manage"),
            TrainingSession s => s.MemberId == MyMemberId || s.CoachId == MyCoachId || Has("coaches:manage"),
            TimeRequest r => (r.MemberId is not null && r.MemberId == MyMemberId) || r.CoachId == MyCoachId || Has("coaches:manage")
                || (r.MemberId is null && All<CoachHire>().Any(h => h.CoachId == r.CoachId && h.MemberId == MyMemberId && h.Status == Status.Active)),
            ChatMessage c => c.MemberId == MyMemberId || c.CoachId == MyCoachId, // private: not even admins read chats
            Discount => Has("packages:manage"), // members only learn a code is valid by using it
            MembershipPackage k => k.IsActive || Has("packages:manage"),
            Amenity a => a.IsActive || Has("amenities:manage"),
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

    public Task<T> InsertAsync<T>(T row) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException<T>(FailWith);
        const string rls = "new row violates row-level security policy";
        try
        {
            switch (row)
            {
                case CoachHire h when h.MemberId != MyMemberId || !ActiveMembership(Uid): throw new Exception(rls);
                case TimeRequest r when (r.RequestedBy == "member" && r.MemberId != MyMemberId)
                                      || (r.RequestedBy == "coach" && r.CoachId != MyCoachId): throw new Exception(rls);
                case AmenityUsage u when !All<UserAmenity>().Any(a => a.Id == u.UserAmenityId && a.UserId == Uid): throw new Exception(rls);
                case UserNotification or AuditEntry or Payment or TrainingSession: throw new Exception(rls);
                case Profile or Role or UserRole: if (!Has("users:manage")) throw new Exception(rls); break;
                case MembershipPackage: Require("packages:manage"); break;
                case Discount: Require("packages:manage"); break;
                case Amenity: Require("amenities:manage"); break;
                case ShiftPost s when s.CoachId != MyCoachId: throw new Exception(rls);
                case ChatMessage c when c.SenderUserId != Uid || !(c.MemberId == MyMemberId || c.CoachId == MyCoachId)
                                      || !All<CoachHire>().Any(h => h.CoachId == c.CoachId && h.MemberId == c.MemberId && h.Status == Status.Active):
                    throw new Exception("no_active_hire");
            }
        }
        catch (Exception ex) { return Task.FromException<T>(ex); }

        var added = Add(row);
        switch (added)
        {
            case AmenityUsage u: u.UsedAt = DateTime.UtcNow; break;
            case ChatMessage c:
                c.SentAt = DateTime.UtcNow;
                var coachUser = All<Coach>().First(x => x.Id == c.CoachId).UserId;
                Notify(c.SenderUserId == coachUser ? All<Member>().First(x => x.Id == c.MemberId).UserId : coachUser, "message", "New message");
                break;
            case ShiftPost s:
                foreach (var h in All<CoachHire>().Where(h => h.CoachId == s.CoachId && h.Status == Status.Active))
                    Notify(All<Member>().First(m => m.Id == h.MemberId).UserId, "shift", "Your coach posted a shift");
                break;
            case TimeRequest r:
                var target = r.RequestedBy == "member"
                    ? All<Coach>().First(c => c.Id == r.CoachId).UserId
                    : All<Member>().FirstOrDefault(m => m.Id == r.MemberId)?.UserId;
                if (target is not null) Notify(target, "time_request", "New time request");
                break;
        }
        return Task.FromResult(Clone(added));
    }

    public Task UpdateAsync<T>(T row) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        var pk = Pk(typeof(T));
        var key = pk.GetValue(row);
        var list = Table(typeof(T));
        var index = list.FindIndex(r => Equals(pk.GetValue(r), key));
        if (index < 0) return Task.FromException(new Exception("not_found"));

        switch (row)
        {
            case Payment p: // members may only attach proof
                ((Payment)list[index]).ProofPath = p.ProofPath;
                return Task.CompletedTask;
            case MembershipPackage: Require("packages:manage"); break;
            case Discount: Require("packages:manage"); break;
            case ShiftPost when ((ShiftPost)list[index]).CoachId != MyCoachId: throw new Exception("forbidden");
            case Amenity: Require("amenities:manage"); break;
            case Profile prof when ((Profile)list[index]).IsActive != prof.IsActive: Require("users:manage"); break;
        }
        list[index] = Clone(row);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(T row) where T : BaseModel, new()
    {
        if (FailWith is not null) return Task.FromException(FailWith);
        try
        {
            switch (row)
            {
                case MembershipPackage p:
                    Require("packages:manage");
                    if (All<UserMembershipPackage>().Any(m => m.MembershipPackageId == p.Id)) throw new Exception("in_use"); // same as a foreign-key block
                    Audit("package.delete", "membership_packages", p.Id.ToString());
                    break;
                case Discount: Require("packages:manage"); break;
                default: throw new Exception("forbidden");
            }
            var key = Pk(typeof(T)).GetValue(row);
            Table(typeof(T)).RemoveAll(r => Equals(Pk(typeof(T)).GetValue(r), key));
            return Task.CompletedTask;
        }
        catch (Exception ex) { return Task.FromException(ex); }
    }

    public Task<string> UploadAsync(string bucket, string path, byte[] data) =>
        FailWith is null ? Task.FromResult(path) : Task.FromException<string>(FailWith);

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

    // ---------- RPCs (mirror supabase/migrations once the DB exists) ----------
    object? Call(string fn, Dictionary<string, object> a)
    {
        var now = DateTime.UtcNow;
        int I(string k) => Convert.ToInt32(a[k]);
        bool B(string k) => Convert.ToBoolean(a[k]);
        string S(string k) => (string)a[k];
        _ = Uid;

        switch (fn)
        {
            case "avail_membership":
            {
                var pkg = All<MembershipPackage>().FirstOrDefault(p => p.Id == I("p_package_id") && p.IsActive) ?? throw new Exception("not_found");
                if (All<UserMembershipPackage>().Any(m => m.UserId == Uid && m.MembershipPackageId == pkg.Id && (m.Status == "pending" || m.Status == "active")))
                    throw new Exception("already_availed");
                var (amount, code) = Discounted(pkg.Price, a);
                var ump = Add(new UserMembershipPackage { UserId = Uid, MembershipPackageId = pkg.Id, Status = "pending" });
                return Add(new Payment { Qr = Guid.NewGuid().ToString("N"), Amount = amount, DiscountCode = code, Currency = pkg.Currency, UserId = Uid, UserMembershipPackageId = ump.Id, CreatedAt = now }).Id;
            }
            case "renew_membership":
            {
                var ump = All<UserMembershipPackage>().FirstOrDefault(m => m.Id == I("p_ump_id") && m.UserId == Uid) ?? throw new Exception("not_found");
                if (ump.Status is "pending" or "cancelled") throw new Exception("not_renewable");
                if (_renewalPayments.Any(r => r.Value == ump.Id && All<Payment>().First(p => p.Id == r.Key).Status == "pending")) throw new Exception("renewal_pending");
                var pkg = All<MembershipPackage>().First(p => p.Id == ump.MembershipPackageId);
                var (amount, code) = Discounted(pkg.Price, a);
                var pay = Add(new Payment { Qr = Guid.NewGuid().ToString("N"), Amount = amount, DiscountCode = code, Currency = pkg.Currency, UserId = Uid, UserMembershipPackageId = ump.Id, CreatedAt = now });
                _renewalPayments[pay.Id] = ump.Id;
                return pay.Id;
            }
            case "verify_payment":
            {
                Require("payments:verify");
                var approve = B("p_approve");
                var pay = All<Payment>().FirstOrDefault(p => p.Id == I("p_payment_id")) ?? throw new Exception("not_found");
                if (pay.Status != "pending") throw new Exception("already_processed");
                if (pay.UserId == Uid) throw new Exception("self_verify");
                pay.Status = approve ? Status.Verified : Status.Rejected;
                pay.VerifiedAt = now;
                if (pay.UserMembershipPackageId is { } umpId)
                {
                    var ump = All<UserMembershipPackage>().First(m => m.Id == umpId);
                    var pkg = All<MembershipPackage>().First(p => p.Id == ump.MembershipPackageId);
                    var renewal = _renewalPayments.ContainsKey(pay.Id);
                    if (approve && renewal)
                    {
                        var from = ump.EndsAt is { } e && e > now ? e : now; // an expired term renews from today, not from the past end date
                        ump.EndsAt = from.AddDays(pkg.DurationDays); ump.Status = Status.Active;
                    }
                    else if (approve) { ump.StartsAt = now; ump.EndsAt = now.AddDays(pkg.DurationDays); ump.Status = Status.Active; }
                    else if (!renewal) ump.Status = Status.Cancelled;
                }
                Notify(pay.UserId, "payment", approve ? "Payment verified" : "Payment rejected");
                Audit(approve ? "payment.verify" : "payment.reject", "payments", pay.Id.ToString());
                return null;
            }
            case "avail_amenity":
            {
                if (!ActiveMembership(Uid)) throw new Exception("no_active_membership");
                var id = I("p_amenity_id");
                if (!All<Amenity>().Any(x => x.Id == id && x.IsActive)) throw new Exception("not_found");
                return (All<UserAmenity>().FirstOrDefault(x => x.UserId == Uid && x.AmenityId == id) ?? Add(new UserAmenity { UserId = Uid, AmenityId = id })).Id;
            }
            case "verify_amenity_usage":
            {
                Require("amenities:verify");
                var u = All<AmenityUsage>().FirstOrDefault(x => x.Id == I("p_id")) ?? throw new Exception("not_found");
                if (u.Status != "pending") throw new Exception("already_processed");
                u.Status = B("p_approve") ? Status.Verified : Status.Rejected;
                Notify(All<UserAmenity>().First(x => x.Id == u.UserAmenityId).UserId, "attendance", B("p_approve") ? "Amenity usage verified" : "Amenity usage rejected");
                Audit(B("p_approve") ? "amenity_usage.verify" : "amenity_usage.reject", "amenity_usages", u.Id.ToString());
                return null;
            }
            case "respond_time_request":
            {
                var r = All<TimeRequest>().FirstOrDefault(x => x.Id == I("p_id")) ?? throw new Exception("not_found");
                if (r.Status != "pending") throw new Exception("already_processed");
                var coach = All<Coach>().First(c => c.Id == r.CoachId);
                string requester;
                if (r.RequestedBy == "member")
                {
                    if (coach.UserId != Uid) throw new Exception("forbidden");
                    requester = All<Member>().First(m => m.Id == r.MemberId).UserId;
                }
                else
                {
                    if (r.MemberId is null)
                    {
                        if (MyMemberId is not { } me || !All<CoachHire>().Any(h => h.CoachId == r.CoachId && h.MemberId == me && h.Status == "active")) throw new Exception("forbidden");
                        r.MemberId = me;
                    }
                    else if (r.MemberId != MyMemberId) throw new Exception("forbidden");
                    requester = coach.UserId;
                }
                if (B("p_approve"))
                {
                    if (All<TrainingSession>().Any(s => s.Id != r.SessionId && s.CoachId == r.CoachId && s.Status != "cancelled" && s.ScheduledStart < r.RequestedEnd && r.RequestedStart < s.ScheduledEnd))
                        throw new Exception("overlap");
                    if (r.SessionId is { } sid)
                    {   // a reschedule moves the booked session instead of creating a second one
                        var moved = All<TrainingSession>().First(s => s.Id == sid);
                        if (moved.Status != Status.Scheduled) throw new Exception("already_processed");
                        moved.ScheduledStart = r.RequestedStart; moved.ScheduledEnd = r.RequestedEnd;
                    }
                    else Add(new TrainingSession { CoachId = r.CoachId, MemberId = r.MemberId!.Value, Title = "Training session", ScheduledStart = r.RequestedStart, ScheduledEnd = r.RequestedEnd });
                    r.Status = Status.Approved;
                }
                else r.Status = Status.Rejected;
                Notify(requester, "time_request", B("p_approve") ? "Time request approved" : "Time request rejected");
                return null;
            }
            case "record_session_attendance":
            {
                var s = All<TrainingSession>().FirstOrDefault(x => x.Id == I("p_session_id")) ?? throw new Exception("not_found");
                if (!(Has("attendance:record") || s.CoachId == MyCoachId)) throw new Exception("forbidden");
                if (s.MemberId != I("p_member_id")) throw new Exception("wrong_member");
                _attendance[(s.Id, s.MemberId)] = S("p_status");
                return null;
            }
            case "record_check_in":
            {
                Require("checkins:record");
                var m = All<Member>().FirstOrDefault(x => x.Id == I("p_member_id")) ?? throw new Exception("not_found");
                if (!ActiveMembership(m.UserId)) throw new Exception("no_active_membership");
                _checkIns.Add((m.Id, now));
                return null;
            }
            case "assign_role":
            {
                Require("users:manage");
                var role = All<Role>().FirstOrDefault(r => r.Name == S("p_role")) ?? throw new Exception("not_found");
                if (!All<UserRole>().Any(u => u.UserId == S("p_user") && u.RoleId == role.RoleId))
                    Add(new UserRole { UserId = S("p_user"), RoleId = role.RoleId });
                if (role.Name == "coach" && All<Coach>().All(c => c.UserId != S("p_user"))) Add(new Coach { UserId = S("p_user") });
                Audit("role.assign", "user_roles", S("p_user"));
                return null;
            }
            case "revoke_role":
            {
                Require("users:manage");
                if (S("p_user") == Uid && S("p_role") == "admin") throw new Exception("forbidden");
                var role = All<Role>().First(r => r.Name == S("p_role"));
                Table(typeof(UserRole)).RemoveAll(u => ((UserRole)u).UserId == S("p_user") && ((UserRole)u).RoleId == role.RoleId);
                Audit("role.revoke", "user_roles", S("p_user"));
                return null;
            }
            case "set_user_active":
            {
                Require("users:manage");
                All<Profile>().First(p => p.Id == S("p_user")).IsActive = B("p_active");
                Audit("user.active", "profiles", S("p_user"));
                return null;
            }
            case "revenue_summary":
            {
                Require("revenue:read");
                var from = DateTime.Parse(S("p_from")).Date; var to = DateTime.Parse(S("p_to")).Date;
                return All<Payment>().Where(p => p.Status == "verified" && p.VerifiedAt!.Value.Date >= from && p.VerifiedAt.Value.Date <= to)
                    .GroupBy(p => (p.VerifiedAt!.Value.Date, p.Currency)).OrderBy(g => g.Key.Date)
                    .Select(g => new RevenueRow(g.Key.Date, g.Key.Currency, g.Sum(p => p.Amount))).ToList();
            }
            case "request_reschedule":
            {
                var s = All<TrainingSession>().FirstOrDefault(x => x.Id == I("p_session_id")) ?? throw new Exception("not_found");
                var byCoach = s.CoachId == MyCoachId;
                if (!byCoach && s.MemberId != MyMemberId) throw new Exception("forbidden");
                if (s.Status != Status.Scheduled || All<TimeRequest>().Any(r => r.SessionId == s.Id && r.Status == "pending")) throw new Exception("already_processed");
                var start = DateTime.Parse(S("p_start"), null, System.Globalization.DateTimeStyles.RoundtripKind);
                var end = DateTime.Parse(S("p_end"), null, System.Globalization.DateTimeStyles.RoundtripKind);
                if (end <= start) throw new Exception("end must be after start");
                Add(new TimeRequest { CoachId = s.CoachId, MemberId = s.MemberId, SessionId = s.Id, RequestedBy = byCoach ? "coach" : "member",
                                      RequestedStart = start, RequestedEnd = end, Message = "Move this session" });
                Notify(byCoach ? All<Member>().First(m => m.Id == s.MemberId).UserId : All<Coach>().First(c => c.Id == s.CoachId).UserId,
                       "time_request", "Reschedule requested");
                return null;
            }
            case "cancel_session":
            {
                var s = All<TrainingSession>().FirstOrDefault(x => x.Id == I("p_session_id")) ?? throw new Exception("not_found");
                var byCoach = s.CoachId == MyCoachId;
                if (!byCoach && s.MemberId != MyMemberId) throw new Exception("forbidden");
                if (s.Status != Status.Scheduled) throw new Exception("already_processed");
                s.Status = Status.Cancelled;
                foreach (var req in All<TimeRequest>().Where(x => x.SessionId == s.Id && x.Status == "pending")) req.Status = Status.Cancelled;
                Notify(byCoach ? All<Member>().First(m => m.Id == s.MemberId).UserId : All<Coach>().First(c => c.Id == s.CoachId).UserId,
                       "session", "A training session was cancelled");
                return null;
            }
            case "create_user":
            {
                Require("users:manage");
                var email = S("p_email").Trim(); var role = S("p_role");
                if (role is not ("member" or "coach" or "employee") || string.IsNullOrWhiteSpace(S("p_name")) || S("p_password").Length < 6) throw new Exception("bad_input");
                if (_accounts.ContainsKey(email)) throw new Exception("email_taken");
                var id = AddUser("u-" + Guid.NewGuid().ToString("N")[..8], S("p_name").Trim(), email, role == "member" ? Array.Empty<string>() : new[] { role });
                _passwords[id] = S("p_password");
                if (role == "coach") Add(new Coach { UserId = id });
                Audit("user.create", "profiles", id);
                return null;
            }
            case "change_password":
            {
                var me = Uid ?? throw new Exception("not authenticated");
                if (S("p_current") != _passwords.GetValueOrDefault(me, DemoPassword)) throw new Exception("wrong_password");
                if (S("p_new").Length < 6) throw new Exception("bad_input");
                _passwords[me] = S("p_new");
                return null;
            }
            case "reset_password":
            {
                Require("users:manage");
                var id = S("p_user");
                if (S("p_password").Length < 6) throw new Exception("bad_input");
                if (All<Profile>().All(p => p.Id != id)) throw new Exception("not_found");
                _passwords[id] = S("p_password");
                Notify(id, "account", "Your password was reset. Sign in with the new one.");
                Audit("user.reset_password", "profiles", id);
                return null;
            }
            default: throw new Exception("not_found");
        }
    }

    /// <summary>Price after an optional percent-off code. A wrong or expired code is an error, never a silent full price.</summary>
    (decimal Amount, string? Code) Discounted(decimal price, Dictionary<string, object> args)
    {
        if (!args.TryGetValue("p_code", out var raw) || string.IsNullOrWhiteSpace(raw as string)) return (price, null);
        var code = ((string)raw).Trim();
        var d = All<Discount>().FirstOrDefault(x => x.IsActive && x.ExpiresAt > DateTime.UtcNow && string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception("invalid_code");
        return (Math.Round(price * (100 - d.Percent) / 100m, 2), d.Code);
    }

    // ---------- seed ----------
    string AddUser(string id, string name, string email, params string[] roles)
    {
        _accounts[email] = id;
        Add(new Profile { Id = id, Username = name });
        Add(new UserSettings { UserId = id });
        Add(new Member { UserId = id });
        foreach (var r in roles.Append("member").Distinct())
            Add(new UserRole { UserId = id, RoleId = All<Role>().First(x => x.Name == r).RoleId });
        return id;
    }

    void Seed()
    {
        var now = DateTime.UtcNow;
        DateTime At(int days, double hour) => DateTime.Today.AddDays(days).AddHours(hour).ToUniversalTime(); // seed in the viewer's local time
        foreach (var r in new[] { "admin", "employee", "coach", "member" }) Add(new Role { Name = r });

        AddUser("u-admin", "Dana Reyes", "admin@demo.io", "admin");
        AddUser("u-emp", "Marco Santos", "employee@demo.io", "employee");
        AddUser("u-coach1", "Lena Cruz", "coach@demo.io", "coach");
        AddUser("u-coach2", "Kai Mendoza", "kai@demo.io", "coach");
        AddUser("u-alex", "Alex Tan", "member@demo.io");
        AddUser("u-bea", "Bea Lim", "bea@demo.io");
        AddUser("u-carlo", "Carlo Diaz", "carlo@demo.io");

        Add(new Coach { UserId = "u-coach1", Specialty = "Strength and conditioning", HourlyRate = 25, Bio = "Ten years coaching powerlifting and general strength. Works with beginners and returning lifters." });
        Add(new Coach { UserId = "u-coach2", Specialty = "Boxing and mobility", HourlyRate = 22, Bio = "Former amateur boxer. Sessions focus on footwork, conditioning and joint mobility." });

        var monthly = Add(new MembershipPackage { Name = "Monthly", Description = "Full gym floor access for 30 days.", Price = 30, DurationDays = 30 });
        var quarterly = Add(new MembershipPackage { Name = "Quarterly", Description = "90 days of access at a lower monthly rate.", Price = 80, DurationDays = 90 });
        Add(new MembershipPackage { Name = "Annual", Description = "A full year, best value for regulars.", Price = 280, DurationDays = 365 });

        var pool = Add(new Amenity { Name = "Pool", Description = "25 m lap pool, open 6:00 to 21:00." });
        Add(new Amenity { Name = "Sauna", Description = "Dry sauna next to the locker rooms." });
        Add(new Amenity { Name = "Group classes", Description = "Spin, yoga and circuit classes on the studio floor." });
        Add(new Amenity { Name = "Boxing ring", Description = "Closed for maintenance.", IsActive = false });

        // Alex: active, 5 days left so the renew prompt shows
        var alexMembership = Add(new UserMembershipPackage { UserId = "u-alex", MembershipPackageId = monthly.Id, Status = "active", StartsAt = now.AddDays(-25), EndsAt = now.AddDays(5) });
        // Bea: waiting for payment verification
        var beaMembership = Add(new UserMembershipPackage { UserId = "u-bea", MembershipPackageId = quarterly.Id, Status = "pending" });
        Add(new Payment { Qr = "demo-qr-bea", Amount = quarterly.Price, UserId = "u-bea", UserMembershipPackageId = beaMembership.Id, CreatedAt = now.AddHours(-3), ProofPath = "u-bea/slip.jpg" });
        // Carlo: expired
        var carloMembership = Add(new UserMembershipPackage { UserId = "u-carlo", MembershipPackageId = monthly.Id, Status = "expired", StartsAt = now.AddDays(-42), EndsAt = now.AddDays(-12) });

        // verified history for the revenue screen
        void Paid(string user, int umpId, decimal amount, int daysAgo, string qr) =>
            Add(new Payment { Qr = qr, Amount = amount, UserId = user, UserMembershipPackageId = umpId, Status = "verified", CreatedAt = now.AddDays(-daysAgo), VerifiedAt = now.AddDays(-daysAgo) });
        Paid("u-alex", alexMembership.Id, 30, 25, "demo-h1"); Paid("u-carlo", carloMembership.Id, 30, 42, "demo-h2");
        Paid("u-alex", alexMembership.Id, 80, 9, "demo-h3"); Paid("u-carlo", carloMembership.Id, 280, 6, "demo-h4");
        Paid("u-alex", alexMembership.Id, 30, 3, "demo-h5"); Paid("u-carlo", carloMembership.Id, 80, 1, "demo-h6");

        var alexPool = Add(new UserAmenity { UserId = "u-alex", AmenityId = pool.Id });
        Add(new AmenityUsage { UserAmenityId = alexPool.Id, UsedAt = now.AddHours(-2), Proof = "u-alex/pool.jpg" });

        var alexMember = All<Member>().First(m => m.UserId == "u-alex");
        Add(new CoachHire { MemberId = alexMember.Id, CoachId = 1 });

        Add(new TimeRequest { CoachId = 1, MemberId = null, RequestedBy = "coach", RequestedStart = At(1, 10), RequestedEnd = At(1, 11), Message = "Open slot, strength session" });
        Add(new TimeRequest { CoachId = 1, MemberId = alexMember.Id, RequestedBy = "member", RequestedStart = At(2, 17), RequestedEnd = At(2, 18), Message = "Evening session after work?" });

        Add(new TrainingSession { CoachId = 1, MemberId = alexMember.Id, Title = "Squat technique", ScheduledStart = At(2, 9), ScheduledEnd = At(2, 10) });
        Add(new TrainingSession { CoachId = 1, MemberId = alexMember.Id, Title = "Upper body", ScheduledStart = At(-3, 9), ScheduledEnd = At(-3, 10), Status = "completed" });

        Add(new UserNotification { UserId = "u-alex", Type = "payment", Title = "Payment verified", CreatedAt = now.AddDays(-25), IsRead = true });
        Add(new UserNotification { UserId = "u-alex", Type = "membership", Title = "Membership expires in 5 days", Body = "Renew to keep your access.", CreatedAt = now.AddHours(-6) });
        Add(new UserNotification { UserId = "u-coach1", Type = "time_request", Title = "New time request", CreatedAt = now.AddHours(-1) });
        Add(new UserNotification { UserId = "u-emp", Type = "payment", Title = "New payment waiting for verification", CreatedAt = now.AddHours(-3) });

        Add(new Discount { Code = "WELCOME10", Percent = 10, ExpiresAt = now.AddDays(60) });
        Add(new Discount { Code = "NEWYEAR20", Percent = 20, ExpiresAt = now.AddDays(-5) });

        var beaMember = All<Member>().First(m => m.UserId == "u-bea");
        Add(new CoachHire { MemberId = beaMember.Id, CoachId = 1 });

        Add(new ShiftPost { CoachId = 1, Start = At(1, 6), End = At(1, 14), Note = "Strength floor, squat rack priority" });
        Add(new ShiftPost { CoachId = 1, Start = At(3, 14), End = At(3, 20) });
        Add(new ShiftPost { CoachId = 2, Start = At(1, 14), End = At(1, 22), Note = "Heavy bags and pad work" });

        Add(new ChatMessage { CoachId = 1, MemberId = alexMember.Id, SenderUserId = "u-alex", Body = "Hi Lena, can we work on my squat depth this week?", SentAt = now.AddHours(-26) });
        Add(new ChatMessage { CoachId = 1, MemberId = alexMember.Id, SenderUserId = "u-coach1", Body = "Sure. Bring a flat-soled pair of shoes and we'll film it from the side.", SentAt = now.AddHours(-25) });
        Add(new ChatMessage { CoachId = 1, MemberId = alexMember.Id, SenderUserId = "u-alex", Body = "Perfect, see you on the session.", SentAt = now.AddHours(-24) });

        Add(new AuditEntry { ActorId = "u-emp", Action = "payment.verify", Entity = "payments", EntityId = "5", CreatedAt = now.AddDays(-3) });
        Add(new AuditEntry { ActorId = "u-admin", Action = "role.assign", Entity = "user_roles", EntityId = "u-coach2", CreatedAt = now.AddDays(-8) });
    }
}
