using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

static class Directory_
{
    public static async Task<Dictionary<string, string>> UserNamesAsync(ISupabaseGateway gw) =>
        (await gw.ListAsync<Profile>()).ToDictionary(p => p.Id, p => p.Username);
}

// ===================== Auth =====================
public interface IAuthService
{
    string? UserId { get; }
    Task<AppResult<AppRole>> SignInAsync(string email, string password);
    Task SignOutAsync();
}

public sealed class AuthService(ISupabaseGateway gw) : IAuthService
{
    public string? UserId => gw.CurrentUserId;

    public Task<AppResult<AppRole>> SignInAsync(string email, string password) => Safe.RunAsync(async () =>
    {
        await gw.SignInAsync(email, password);
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var mine = await gw.ListAsync<UserRole>(u => u.UserId == uid);
        var roles = await gw.ListAsync<Role>();
        return RoleResolver.Pick(mine.Select(m => roles.FirstOrDefault(r => r.RoleId == m.RoleId)?.Name).OfType<string>());
    });

    public Task SignOutAsync() => gw.SignOutAsync();
}

// ===================== Membership (member) =====================
public sealed record MembershipRow(int Id, string PackageName, string Status, DateTime? EndsAt, int? DaysLeft, int DurationDays, bool CanRenew, decimal Price, string Currency);
public sealed record PaymentItem(int Id, string What, decimal Amount, string Currency, string Status, DateTime CreatedAt, bool NeedsProof);

public interface IMembershipService
{
    Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync();
    Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync();
    Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync();
    Task<AppResult<int>> AvailAsync(int packageId);
    Task<AppResult<int>> RenewAsync(int membershipId);
    Task<AppResult<bool>> AttachProofAsync(int paymentId, string fileName, byte[] data);
}

public sealed class MembershipService(ISupabaseGateway gw) : IMembershipService
{
    public Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync() =>
        Safe.RunAsync(() => gw.ListAsync<MembershipPackage>(p => p.IsActive == true));

    public static MembershipRow ToRow(UserMembershipPackage m, MembershipPackage? p, DateTime now)
    {
        int? left = m.EndsAt is { } e ? Math.Max(0, (int)Math.Ceiling((e - now).TotalDays)) : null;
        var canRenew = (m.Status == Status.Active && left <= 7) || m.Status == Status.Expired;
        return new(m.Id, p?.Name ?? "Membership", m.Status, m.EndsAt, left, p?.DurationDays ?? 30, canRenew, p?.Price ?? 0, p?.Currency ?? "USD");
    }

    public Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync() => Safe.RunAsync<IReadOnlyList<MembershipRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var mine = await gw.ListAsync<UserMembershipPackage>(m => m.UserId == uid);
        var packages = await gw.ListAsync<MembershipPackage>();
        return mine.OrderByDescending(m => m.EndsAt ?? DateTime.MaxValue)
            .Select(m => ToRow(m, packages.FirstOrDefault(p => p.Id == m.MembershipPackageId), DateTime.UtcNow)).ToList();
    });

    public Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync() => Safe.RunAsync<IReadOnlyList<PaymentItem>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var pays = await gw.ListAsync<Payment>(p => p.UserId == uid);
        var mine = await gw.ListAsync<UserMembershipPackage>(m => m.UserId == uid);
        var packages = await gw.ListAsync<MembershipPackage>();
        string What(Payment p)
        {
            var m = mine.FirstOrDefault(x => x.Id == p.UserMembershipPackageId);
            return packages.FirstOrDefault(x => x.Id == m?.MembershipPackageId)?.Name ?? "Amenity";
        }
        return pays.OrderByDescending(p => p.CreatedAt)
            .Select(p => new PaymentItem(p.Id, What(p), p.Amount, p.Currency, p.Status, p.CreatedAt, p.Status == Status.Pending && p.ProofPath is null)).ToList();
    });

    public Task<AppResult<int>> AvailAsync(int packageId) =>
        Safe.RunAsync(() => gw.RpcAsync<int>("avail_membership", new() { ["p_package_id"] = packageId }));

    public Task<AppResult<int>> RenewAsync(int membershipId) =>
        Safe.RunAsync(() => gw.RpcAsync<int>("renew_membership", new() { ["p_ump_id"] = membershipId }));

    public Task<AppResult<bool>> AttachProofAsync(int paymentId, string fileName, byte[] data) => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var path = await gw.UploadAsync("payment-proofs", $"{uid}/{paymentId}-{fileName}", data);
        var row = (await gw.ListAsync<Payment>(p => p.Id == paymentId)).First();
        row.ProofPath = path;
        await gw.UpdateAsync(row);
        return true;
    });
}

// ===================== Payment verification (staff) =====================
public sealed record PaymentRow(Payment Payment, string Payer, string Item, bool HasProof);

public interface IPaymentStaffService
{
    Task<AppResult<IReadOnlyList<PaymentRow>>> PendingAsync();
    Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve);
}

public sealed class PaymentStaffService(ISupabaseGateway gw) : IPaymentStaffService
{
    public Task<AppResult<IReadOnlyList<PaymentRow>>> PendingAsync() => Safe.RunAsync<IReadOnlyList<PaymentRow>>(async () =>
    {
        var pending = await gw.ListAsync<Payment>(p => p.Status == "pending");
        var names = await Directory_.UserNamesAsync(gw);
        var umps = await gw.ListAsync<UserMembershipPackage>();
        var packages = await gw.ListAsync<MembershipPackage>();
        string Item(Payment p) => packages.FirstOrDefault(k => k.Id == umps.FirstOrDefault(u => u.Id == p.UserMembershipPackageId)?.MembershipPackageId)?.Name ?? "Amenity";
        return pending.OrderBy(p => p.CreatedAt)
            .Select(p => new PaymentRow(p, names.GetValueOrDefault(p.UserId, "Unknown"), Item(p), p.ProofPath is not null)).ToList();
    });

    public Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("verify_payment", new() { ["p_payment_id"] = paymentId, ["p_approve"] = approve });
        return true;
    });
}

// ===================== Amenities =====================
public sealed record AmenityRow(Amenity Amenity, int? UserAmenityId);
public sealed record UsageRow(AmenityUsage Usage, string Member, string Amenity);

public interface IAmenityService
{
    Task<AppResult<IReadOnlyList<AmenityRow>>> CatalogAsync();
    Task<AppResult<int>> AvailAsync(int amenityId);
    Task<AppResult<bool>> LogUsageAsync(int userAmenityId, (string FileName, byte[] Data)? proof);
    Task<AppResult<IReadOnlyList<UsageRow>>> PendingUsagesAsync();
    Task<AppResult<bool>> VerifyUsageAsync(int usageId, bool approve);
}

public sealed class AmenityService(ISupabaseGateway gw) : IAmenityService
{
    public Task<AppResult<IReadOnlyList<AmenityRow>>> CatalogAsync() => Safe.RunAsync<IReadOnlyList<AmenityRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var catalog = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        var mine = await gw.ListAsync<UserAmenity>(a => a.UserId == uid);
        return catalog.Select(a => new AmenityRow(a, mine.FirstOrDefault(m => m.AmenityId == a.Id)?.Id)).ToList();
    });

    public Task<AppResult<int>> AvailAsync(int amenityId) =>
        Safe.RunAsync(() => gw.RpcAsync<int>("avail_amenity", new() { ["p_amenity_id"] = amenityId }));

    public Task<AppResult<bool>> LogUsageAsync(int userAmenityId, (string FileName, byte[] Data)? proof) => Safe.RunAsync(async () =>
    {
        string? path = null;
        if (proof is { } p)
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            path = await gw.UploadAsync("amenity-proofs", $"{uid}/{userAmenityId}-{p.FileName}", p.Data);
        }
        await gw.InsertAsync(new AmenityUsage { UserAmenityId = userAmenityId, Proof = path, Status = "pending" });
        return true;
    });

    public Task<AppResult<IReadOnlyList<UsageRow>>> PendingUsagesAsync() => Safe.RunAsync<IReadOnlyList<UsageRow>>(async () =>
    {
        var pending = await gw.ListAsync<AmenityUsage>(u => u.Status == "pending");
        var ents = await gw.ListAsync<UserAmenity>();
        var amenities = await gw.ListAsync<Amenity>();
        var names = await Directory_.UserNamesAsync(gw);
        return pending.Select(u =>
        {
            var e = ents.FirstOrDefault(x => x.Id == u.UserAmenityId);
            return new UsageRow(u, names.GetValueOrDefault(e?.UserId ?? "", "Unknown"), amenities.FirstOrDefault(a => a.Id == e?.AmenityId)?.Name ?? "Amenity");
        }).ToList();
    });

    public Task<AppResult<bool>> VerifyUsageAsync(int usageId, bool approve) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("verify_amenity_usage", new() { ["p_id"] = usageId, ["p_approve"] = approve });
        return true;
    });
}

// ===================== Coaches =====================
public sealed record CoachCard(Coach Coach, string Name, bool Hired, int? HireId);

public interface ICoachService
{
    Task<AppResult<IReadOnlyList<CoachCard>>> ListCoachesAsync();
    Task<AppResult<bool>> HireAsync(int coachId);
    Task<AppResult<bool>> EndHireAsync(int hireId);
    Task<AppResult<Coach?>> MyCoachAsync();
    Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, decimal? rate, bool available);
}

public sealed class CoachService(ISupabaseGateway gw) : ICoachService
{
    async Task<Member?> MeAsync() { var uid = gw.CurrentUserId; return (await gw.ListAsync<Member>(m => m.UserId == uid)).FirstOrDefault(); }

    public Task<AppResult<IReadOnlyList<CoachCard>>> ListCoachesAsync() => Safe.RunAsync<IReadOnlyList<CoachCard>>(async () =>
    {
        var coaches = await gw.ListAsync<Coach>(c => c.IsAvailable == true);
        var names = await Directory_.UserNamesAsync(gw);
        var me = await MeAsync();
        var hires = me is null ? [] : await gw.ListAsync<CoachHire>(h => h.MemberId == me.Id && h.Status == "active");
        return coaches.Select(c => new CoachCard(c, names.GetValueOrDefault(c.UserId, "Coach"),
            hires.Any(h => h.CoachId == c.Id), hires.FirstOrDefault(h => h.CoachId == c.Id)?.Id)).ToList();
    });

    public Task<AppResult<bool>> HireAsync(int coachId) => Safe.RunAsync(async () =>
    {
        var me = await MeAsync() ?? throw new Exception("not_found");
        await gw.InsertAsync(new CoachHire { MemberId = me.Id, CoachId = coachId, Status = "active" });
        return true;
    });

    public Task<AppResult<bool>> EndHireAsync(int hireId) => Safe.RunAsync(async () =>
    {
        var row = (await gw.ListAsync<CoachHire>(h => h.Id == hireId)).First();
        row.Status = "ended"; row.EndedAt = DateTime.UtcNow;
        await gw.UpdateAsync(row);
        return true;
    });

    public Task<AppResult<Coach?>> MyCoachAsync() => Safe.RunAsync<Coach?>(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault(); });

    public Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, decimal? rate, bool available) => Safe.RunAsync(async () =>
    {
        var c = (await MyCoachAsync()).Value ?? throw new Exception("not_found");
        c.Bio = bio; c.Specialty = specialty; c.HourlyRate = rate; c.IsAvailable = available;
        await gw.UpdateAsync(c);
        return true;
    });
}

// ===================== Time requests and sessions =====================
public sealed record RequestRow(TimeRequest Request, string With, string Kind, bool CanRespond, bool CanCancel);
public sealed record SessionRow(TrainingSession Session, string With, bool IsMine, bool CanManage);
public sealed record RequestsView(IReadOnlyList<RequestRow> Rows, bool IsCoach, IReadOnlyList<CoachCard> HiredCoaches);

public interface ISessionService
{
    Task<AppResult<RequestsView>> RequestsAsync();
    Task<AppResult<bool>> RequestAsync(int coachId, DateTime startUtc, DateTime endUtc, string? message);
    Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc);
    Task<AppResult<bool>> RespondAsync(int requestId, bool approve);
    Task<AppResult<bool>> CancelRequestAsync(int requestId);
    Task<AppResult<IReadOnlyList<SessionRow>>> SessionsAsync();
    Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status);
}

public sealed class SessionService(ISupabaseGateway gw) : ISessionService
{
    public Task<AppResult<RequestsView>> RequestsAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var me = (await gw.ListAsync<Member>(m => m.UserId == uid)).FirstOrDefault();
        var myCoach = (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault();
        var names = await Directory_.UserNamesAsync(gw);
        var coaches = await gw.ListAsync<Coach>();
        var members = await gw.ListAsync<Member>();
        var requests = await gw.ListAsync<TimeRequest>(r => r.Status == "pending");

        string CoachName(int id) => names.GetValueOrDefault(coaches.FirstOrDefault(c => c.Id == id)?.UserId ?? "", "Coach");
        string MemberName(int? id) => id is null ? "Open slot" : names.GetValueOrDefault(members.FirstOrDefault(m => m.Id == id)?.UserId ?? "", "Member");

        var rows = requests.OrderBy(r => r.RequestedStart).Select(r =>
        {
            var iAmCoachOfThis = myCoach?.Id == r.CoachId;
            var mine = (r.RequestedBy == "member" && r.MemberId == me?.Id) || (r.RequestedBy == "coach" && iAmCoachOfThis);
            var canRespond = !mine && (r.RequestedBy == "member" ? iAmCoachOfThis : me is not null);
            var kind = r.RequestedBy == "member" ? "Requested by member" : r.MemberId is null ? "Open availability" : "Offered by coach";
            return new RequestRow(r, iAmCoachOfThis ? MemberName(r.MemberId) : CoachName(r.CoachId), kind, canRespond, mine);
        }).ToList();

        IReadOnlyList<CoachCard> hired = [];
        if (me is not null)
        {
            var hires = await gw.ListAsync<CoachHire>(h => h.MemberId == me.Id && h.Status == "active");
            hired = coaches.Where(c => hires.Any(h => h.CoachId == c.Id)).Select(c => new CoachCard(c, CoachName(c.Id), true, null)).ToList();
        }
        return new RequestsView(rows, myCoach is not null, hired);
    });

    public async Task<AppResult<bool>> RequestAsync(int coachId, DateTime startUtc, DateTime endUtc, string? message)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId;
            var me = (await gw.ListAsync<Member>(m => m.UserId == uid)).FirstOrDefault() ?? throw new Exception("not_found");
            await gw.InsertAsync(new TimeRequest { CoachId = coachId, MemberId = me.Id, RequestedBy = "member", RequestedStart = startUtc, RequestedEnd = endUtc, Message = message });
            return true;
        });
    }

    public async Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId;
            var coach = (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault() ?? throw new Exception("not_found");
            await gw.InsertAsync(new TimeRequest { CoachId = coach.Id, MemberId = null, RequestedBy = "coach", RequestedStart = startUtc, RequestedEnd = endUtc });
            return true;
        });
    }

    public Task<AppResult<bool>> RespondAsync(int requestId, bool approve) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("respond_time_request", new() { ["p_id"] = requestId, ["p_approve"] = approve });
        return true;
    });

    public Task<AppResult<bool>> CancelRequestAsync(int requestId) => Safe.RunAsync(async () =>
    {
        var row = (await gw.ListAsync<TimeRequest>(r => r.Id == requestId)).First();
        row.Status = "cancelled";
        await gw.UpdateAsync(row);
        return true;
    });

    public Task<AppResult<IReadOnlyList<SessionRow>>> SessionsAsync() => Safe.RunAsync<IReadOnlyList<SessionRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var myCoach = (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault();
        var names = await Directory_.UserNamesAsync(gw);
        var coaches = await gw.ListAsync<Coach>();
        var members = await gw.ListAsync<Member>();
        var sessions = await gw.ListAsync<TrainingSession>();
        return sessions.OrderBy(s => s.ScheduledStart).Select(s =>
        {
            var isMine = myCoach?.Id == s.CoachId;
            var with = isMine ? members.FirstOrDefault(m => m.Id == s.MemberId)?.UserId : coaches.FirstOrDefault(c => c.Id == s.CoachId)?.UserId;
            return new SessionRow(s, names.GetValueOrDefault(with ?? "", "Unknown"), isMine, isMine && s.Status == Status.Scheduled);
        }).ToList();
    });

    public Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status) => Safe.RunAsync(async () =>
    {
        var row = (await gw.ListAsync<TrainingSession>(s => s.Id == sessionId)).First();
        row.Status = status;
        await gw.UpdateAsync(row);
        return true;
    });
}

// ===================== Attendance and check-ins =====================
public sealed record MemberHit(int MemberId, string Username, bool HasActiveMembership)
{
    public string PlanStatus => HasActiveMembership ? "active" : "inactive";
}

public interface IAttendanceService
{
    Task<AppResult<bool>> RecordAsync(int sessionId, int memberId, string status);
    Task<AppResult<bool>> CheckInAsync(int memberId);
    Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string usernameFragment);
}

public sealed class AttendanceService(ISupabaseGateway gw) : IAttendanceService
{
    public Task<AppResult<bool>> RecordAsync(int sessionId, int memberId, string status) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("record_session_attendance", new() { ["p_session_id"] = sessionId, ["p_member_id"] = memberId, ["p_status"] = status });
        return true;
    });

    public Task<AppResult<bool>> CheckInAsync(int memberId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("record_check_in", new() { ["p_member_id"] = memberId }); return true; });

    public Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string fragment) => Safe.RunAsync<IReadOnlyList<MemberHit>>(async () =>
    {
        var members = await gw.ListAsync<Member>();
        var profiles = await gw.ListAsync<Profile>();
        var active = (await gw.ListAsync<UserMembershipPackage>(m => m.Status == "active")).Select(m => m.UserId).ToHashSet();
        return members.Join(profiles, m => m.UserId, p => p.Id, (m, p) => new MemberHit(m.Id, p.Username, active.Contains(m.UserId)))
            .Where(h => h.Username.Contains(fragment, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
    });
}

// ===================== Notifications =====================
public interface INotificationService
{
    Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync();
    Task<AppResult<bool>> MarkReadAsync(int id);
}

public sealed class NotificationService(ISupabaseGateway gw) : INotificationService
{
    public Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        return (IReadOnlyList<UserNotification>)(await gw.ListAsync<UserNotification>(n => n.UserId == uid)).OrderByDescending(n => n.CreatedAt).ToList();
    });

    public Task<AppResult<bool>> MarkReadAsync(int id) => Safe.RunAsync(async () =>
    {
        var row = (await gw.ListAsync<UserNotification>(n => n.Id == id)).First();
        row.IsRead = true;
        await gw.UpdateAsync(row);
        return true;
    });
}

// ===================== Admin =====================
public sealed record UserRow(string UserId, string Username, bool IsActive, IReadOnlyList<string> Roles)
{
    public string RoleText => string.Join(", ", Roles);
    public bool IsCoach => Roles.Contains("coach");
    public bool IsEmployee => Roles.Contains("employee");
    public string CoachActionText => IsCoach ? "Remove coach" : "Make coach";
    public string EmployeeActionText => IsEmployee ? "Remove employee" : "Make employee";
    public string ActiveActionText => IsActive ? "Deactivate" : "Reactivate";
    public string ActiveStatus => IsActive ? "active" : "inactive";
}
public sealed record AuditRow(AuditEntry Entry, string Actor);

public interface IAdminService
{
    Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync();
    Task<AppResult<bool>> AssignRoleAsync(string userId, string role);
    Task<AppResult<bool>> RevokeRoleAsync(string userId, string role);
    Task<AppResult<bool>> SetActiveAsync(string userId, bool active);
    Task<AppResult<bool>> SavePackageAsync(MembershipPackage package);
    Task<AppResult<bool>> SaveAmenityAsync(Amenity amenity);
    Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync();
    Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync();
    Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to);
    Task<AppResult<IReadOnlyList<AuditRow>>> AuditAsync();
}

public sealed class AdminService(ISupabaseGateway gw) : IAdminService
{
    public Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync() => Safe.RunAsync<IReadOnlyList<UserRow>>(async () =>
    {
        var profiles = await gw.ListAsync<Profile>();
        var roles = await gw.ListAsync<Role>();
        var links = await gw.ListAsync<UserRole>();
        return profiles.Select(p => new UserRow(p.Id, p.Username, p.IsActive,
            links.Where(l => l.UserId == p.Id).Select(l => roles.First(r => r.RoleId == l.RoleId).Name).OrderBy(n => n).ToList())).ToList();
    });

    public Task<AppResult<bool>> AssignRoleAsync(string userId, string role) => Rpc("assign_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> RevokeRoleAsync(string userId, string role) => Rpc("revoke_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> SetActiveAsync(string userId, bool active) => Rpc("set_user_active", new() { ["p_user"] = userId, ["p_active"] = active });

    public Task<AppResult<bool>> SavePackageAsync(MembershipPackage p) =>
        Safe.RunAsync(async () => { if (p.Id == 0) await gw.InsertAsync(p); else await gw.UpdateAsync(p); return true; });
    public Task<AppResult<bool>> SaveAmenityAsync(Amenity a) =>
        Safe.RunAsync(async () => { if (a.Id == 0) await gw.InsertAsync(a); else await gw.UpdateAsync(a); return true; });

    public Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync() => Safe.RunAsync(() => gw.ListAsync<MembershipPackage>());
    public Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync() => Safe.RunAsync(() => gw.ListAsync<Amenity>());

    public async Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to)
    {
        if (to < from) return AppResult<IReadOnlyList<RevenueRow>>.Fail(AppErrorKind.Validation, "to before from");
        return await Safe.RunAsync(async () => (IReadOnlyList<RevenueRow>)(await gw.RpcAsync<List<RevenueRow>>("revenue_summary",
            new() { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd") }) ?? []));
    }

    public Task<AppResult<IReadOnlyList<AuditRow>>> AuditAsync() => Safe.RunAsync<IReadOnlyList<AuditRow>>(async () =>
    {
        var names = await Directory_.UserNamesAsync(gw);
        return (await gw.ListAsync<AuditEntry>()).OrderByDescending(a => a.CreatedAt)
            .Select(a => new AuditRow(a, names.GetValueOrDefault(a.ActorId ?? "", "System"))).ToList();
    });

    Task<AppResult<bool>> Rpc(string fn, Dictionary<string, object> args) =>
        Safe.RunAsync(async () => { await gw.RpcAsync(fn, args); return true; });
}

// ===================== Settings =====================
public interface ISettingsService
{
    Task<AppResult<UserSettings>> LoadAsync();
    Task<AppResult<bool>> SaveThemeAsync(string theme);
}

public sealed class SettingsService(ISupabaseGateway gw) : ISettingsService
{
    static readonly string[] Themes = ["system", "light", "dark"];

    public Task<AppResult<UserSettings>> LoadAsync() => Safe.RunAsync(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<UserSettings>(s => s.UserId == uid)).First(); });

    public async Task<AppResult<bool>> SaveThemeAsync(string theme)
    {
        if (!Themes.Contains(theme)) return AppResult<bool>.Fail(AppErrorKind.Validation, "unknown theme");
        return await Safe.RunAsync(async () =>
        {
            var s = (await LoadAsync()).Value ?? throw new Exception("not_found");
            s.Theme = theme;
            await gw.UpdateAsync(s);
            return true;
        });
    }
}
