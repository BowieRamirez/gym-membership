using System.Globalization;
using System.Text.RegularExpressions;
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

static class Directory_
{
    /// <summary>Every profile the caller may read, by id. The full name, or the username if none was given.</summary>
    public static async Task<Dictionary<string, string>> UserNamesAsync(ISupabaseGateway gw) =>
        (await gw.ListAsync<Profile>()).ToDictionary(p => p.Id, p => string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName);
}

// ===================== Auth =====================
public interface IAuthService
{
    string? UserId { get; }
    Task<AppResult<AppRole>> SignInAsync(string email, string password);
    /// <summary>Picks up the session saved from the last run. Null means nobody was signed in.</summary>
    Task<AppResult<AppRole?>> RestoreAsync();
    Task SignOutAsync();
    Task<AppResult<bool>> ChangePasswordAsync(string current, string next);
    /// <summary>True while the person still has the temporary password the owner gave them.</summary>
    Task<AppResult<bool>> MustChangePasswordAsync();
    /// <summary>Open sign-up: creates a member account and signs it in. It stays a guest until a payment is verified.</summary>
    Task<AppResult<bool>> RegisterAsync(string name, string email, string password);
}

/// <summary>What a new account may use. The entry caps on the register page read the same numbers.</summary>
public static partial class AccountRules
{
    public const int MinName = 2, MaxName = 60, MaxEmailLocal = 64, MaxEmail = 100, MinPassword = 6, MaxPassword = 64;

    // letters (any script), joined by single spaces, hyphens or apostrophes: "Mary-Ann O'Brien", "Juan dela Cruz"
    [GeneratedRegex(@"^[\p{L}\p{M}]+(?:[ '\-][\p{L}\p{M}]+)*$")] private static partial Regex NameShape();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailShape();

    public static bool IsName(string name) => name.Length is >= MinName and <= MaxName && NameShape().IsMatch(name);

    public static bool IsEmail(string email) =>
        email.Length <= MaxEmail && EmailShape().IsMatch(email) && email.IndexOf('@') <= MaxEmailLocal;

    public static bool IsPassword(string password) => password.Length is >= MinPassword and <= MaxPassword;

    /// <summary>What the name field keeps as you type: letters (and their accents), spaces, hyphens and apostrophes.</summary>
    public static string KeepNameChars(string typed) => new(typed.Where(c =>
        char.IsLetter(c) || c is ' ' or '\'' or '-' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark).ToArray());
}

public sealed class AuthService(ISupabaseGateway gw) : IAuthService
{
    public string? UserId => gw.CurrentUserId;

    public async Task<AppResult<bool>> RegisterAsync(string name, string email, string password)
    {
        name = name.Trim(); email = email.Trim();
        if (!AccountRules.IsName(name)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name");
        if (!AccountRules.IsEmail(email)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_email");
        if (!AccountRules.IsPassword(password)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_password");
        return await Safe.RunAsync(async () => { await gw.SignUpAsync(email, password, name); return true; });
    }

    public async Task<AppResult<bool>> ChangePasswordAsync(string current, string next)
    {
        if (next.Length < 6) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () =>
        {
            await gw.ChangePasswordAsync(current, next);
            await gw.RpcAsync("mark_password_changed");
            return true;
        });
    }

    public Task<AppResult<bool>> MustChangePasswordAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        return (await gw.ListAsync<Profile>(p => p.Id == uid)).FirstOrDefault()?.MustChangePassword ?? false;
    });

    public Task<AppResult<AppRole>> SignInAsync(string email, string password) => Safe.RunAsync(async () =>
    {
        await gw.SignInAsync(email, password);
        return await ResolveRoleAsync();
    });

    public Task<AppResult<AppRole?>> RestoreAsync() => Safe.RunAsync<AppRole?>(async () =>
        await gw.RestoreSessionAsync() ? await ResolveRoleAsync() : null);

    async Task<AppRole> ResolveRoleAsync()
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        // Auth itself still lets a deactivated person sign in, so the app turns them away here
        if ((await gw.ListAsync<Profile>(p => p.Id == uid)).FirstOrDefault() is { IsActive: false })
        {
            await gw.SignOutAsync();
            throw new Exception("forbidden");
        }
        var mine = await gw.ListAsync<UserRole>(u => u.UserId == uid);
        var roles = await gw.ListAsync<Role>();
        return RoleResolver.Pick(mine.Select(m => roles.FirstOrDefault(r => r.RoleId == m.RoleId)?.Name).OfType<string>());
    }

    public Task SignOutAsync() => gw.SignOutAsync();
}

// ===================== Payments: shared lookups =====================
/// <summary>Works out what a payment paid for (a package, a renewal, an amenity or a walk-in) and which item to buy again.</summary>
sealed class PaymentLookup
{
    readonly List<UserMembershipPackage> _umps; readonly List<MembershipRenewal> _renewals; readonly List<UserAmenity> _amenities;
    readonly List<MembershipPackage> _packages; readonly List<Amenity> _catalog;

    PaymentLookup(IEnumerable<UserMembershipPackage> u, IEnumerable<MembershipRenewal> r, IEnumerable<UserAmenity> a, IEnumerable<MembershipPackage> p, IEnumerable<Amenity> c)
    { _umps = u.ToList(); _renewals = r.ToList(); _amenities = a.ToList(); _packages = p.ToList(); _catalog = c.ToList(); }

    public static async Task<PaymentLookup> LoadAsync(ISupabaseGateway gw) =>
        new(await gw.ListAsync<UserMembershipPackage>(), await gw.ListAsync<MembershipRenewal>(), await gw.ListAsync<UserAmenity>(),
            await gw.ListAsync<MembershipPackage>(), await gw.ListAsync<Amenity>());

    string PackageName(int umpId) => _packages.FirstOrDefault(k => k.Id == _umps.FirstOrDefault(u => u.Id == umpId)?.MembershipPackageId)?.Name ?? "Membership";

    public string Item(Payment p) => p.Kind switch
    {
        PayKind.Membership => p.UserMembershipPackageId is { } u ? PackageName(u) : "Membership",
        PayKind.Renewal => "Renewal: " + (_renewals.FirstOrDefault(r => r.Id == p.RenewalId) is { } r ? PackageName(r.UserMembershipPackageId) : "Membership"),
        PayKind.Amenity => _catalog.FirstOrDefault(a => a.Id == _amenities.FirstOrDefault(x => x.Id == p.UserAmenityId)?.AmenityId)?.Name ?? "Amenity",
        _ => "Walk-in"
    };

    /// <summary>The id to pass to avail/renew when paying this again: a package id, a membership id or an amenity id.</summary>
    public int? ItemId(Payment p) => p.Kind switch
    {
        PayKind.Membership => _umps.FirstOrDefault(u => u.Id == p.UserMembershipPackageId)?.MembershipPackageId,
        PayKind.Renewal => _renewals.FirstOrDefault(r => r.Id == p.RenewalId)?.UserMembershipPackageId,
        PayKind.Amenity => _amenities.FirstOrDefault(a => a.Id == p.UserAmenityId)?.AmenityId,
        _ => null
    };
}

// ===================== Membership (member) =====================
public sealed record MembershipRow(int Id, string PackageName, string Status, DateTime? EndsAt, int? DaysLeft, int DurationDays, bool CanRenew, decimal Price);

/// <summary>One of the member's payments. QR payments show the code to type into the e-wallet and wait for a reference number and screenshot.</summary>
public sealed record PaymentItem(int Id, string What, string PaymentCode, string Kind, string Method, decimal Amount, decimal DiscountAmount, string Status,
    DateTime CreatedAt, DateTime? ExpiresAt, string? ReferenceNo, bool HasProof, string? ReceiptNo, string? RejectedReason, int? ItemId)
{
    /// <summary>A QR payment that still needs the member to pay, then send the reference number and screenshot.</summary>
    public bool NeedsProof => Status == Models.Status.Pending && Method == PayMethod.Qr && !HasProof && (ExpiresAt is null || ExpiresAt > DateTime.UtcNow);
    public bool AwaitingReview => Status == Models.Status.Pending && HasProof;
    /// <summary>Rejected and expired payments are final; the member pays again with a fresh one.</summary>
    public bool CanPayAgain => Status is Models.Status.Rejected or Models.Status.Expired && ItemId is not null;
    public string Detail => Status switch
    {
        Models.Status.Rejected => RejectedReason ?? "Rejected",
        Models.Status.Expired => "Not paid in time",
        Models.Status.Verified => ReceiptNo ?? "",
        _ when AwaitingReview => "Waiting for the front desk to check it",
        _ => ""
    };
}

public interface IMembershipService
{
    Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync();
    Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync();
    Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync();
    Task<AppResult<Access>> AccessAsync();
    /// <summary>Starts a membership: a pending QR payment with a code to pay. The membership turns on when staff verify it.</summary>
    Task<AppResult<Payment>> AvailAsync(int packageId, string? discountCode = null);
    Task<AppResult<Payment>> RenewAsync(int membershipId, string? discountCode = null);
    /// <summary>After paying by QR: the e-wallet reference number and a screenshot of the payment.</summary>
    Task<AppResult<bool>> SubmitProofAsync(int paymentId, string referenceNo, string fileName, byte[] data);
    /// <summary>Starts a fresh payment for the same item after a rejection or expiry.</summary>
    Task<AppResult<Payment>> PayAgainAsync(PaymentItem item);
    Task<AppResult<GymSettings>> GymAsync();
    /// <summary>A link to the gym's payment QR image, or null if the owner has not uploaded one.</summary>
    Task<AppResult<string?>> QrUrlAsync();
}

/// <summary>What a member may open: full pages need an active plan; a pending payment only changes the wording.</summary>
public sealed record Access(bool Active, bool PaymentPending);

public sealed class MembershipService(ISupabaseGateway gw) : IMembershipService
{
    public Task<AppResult<Access>> AccessAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var now = DateTime.UtcNow;
        var plans = await gw.ListAsync<UserMembershipPackage>(m => m.MemberId == uid);
        var pays = await gw.ListAsync<Payment>(p => p.MemberId == uid);
        return new Access(plans.Any(m => m.Status == Status.Active && m.EndsAt > now), pays.Any(p => p.Status == Status.Pending));
    });

    public Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync() =>
        Safe.RunAsync(() => gw.ListAsync<MembershipPackage>(p => p.IsActive == true));

    public static MembershipRow ToRow(UserMembershipPackage m, MembershipPackage? p, DateTime now)
    {
        int? left = m.EndsAt is { } e ? Math.Max(0, (int)Math.Ceiling((e - now).TotalDays)) : null;
        var canRenew = (m.Status == Status.Active && left <= 7) || m.Status == Status.Expired;
        return new(m.Id, p?.Name ?? "Membership", m.Status, m.EndsAt, left, p?.DurationDays ?? 30, canRenew, p?.Price ?? 0);
    }

    public Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync() => Safe.RunAsync<IReadOnlyList<MembershipRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var mine = await gw.ListAsync<UserMembershipPackage>(m => m.MemberId == uid);
        var packages = await gw.ListAsync<MembershipPackage>();
        // pending and cancelled rows are payments in progress or abandoned: the Payments list shows those
        return mine.Where(m => m.Status is Status.Active or Status.Expired).OrderByDescending(m => m.EndsAt ?? DateTime.MaxValue)
            .Select(m => ToRow(m, packages.FirstOrDefault(p => p.Id == m.MembershipPackageId), DateTime.UtcNow)).ToList();
    });

    public Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync() => Safe.RunAsync<IReadOnlyList<PaymentItem>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var pays = await gw.ListAsync<Payment>(p => p.MemberId == uid);
        var look = await PaymentLookup.LoadAsync(gw);
        return pays.OrderByDescending(p => p.CreatedAt).Select(p => new PaymentItem(p.Id,
            p.DiscountAmount > 0 ? look.Item(p) + " (discount)" : look.Item(p), p.PaymentCode, p.Kind, p.Method, p.Amount, p.DiscountAmount, p.Status,
            p.CreatedAt, p.ExpiresAt, p.ReferenceNo, p.ProofPath is not null, p.ReceiptNo, p.RejectedReason, look.ItemId(p))).ToList();
    });

    static Dictionary<string, object> WithCode(Dictionary<string, object> args, string? code)
    { if (!string.IsNullOrWhiteSpace(code)) args["p_discount_code"] = code.Trim(); return args; }

    public Task<AppResult<Payment>> AvailAsync(int packageId, string? discountCode = null) =>
        Safe.RunAsync(() => gw.RpcAsync<Payment>("avail_membership", WithCode(new() { ["p_package_id"] = packageId }, discountCode)));

    public Task<AppResult<Payment>> RenewAsync(int membershipId, string? discountCode = null) =>
        Safe.RunAsync(() => gw.RpcAsync<Payment>("renew_membership", WithCode(new() { ["p_ump_id"] = membershipId }, discountCode)));

    public async Task<AppResult<bool>> SubmitProofAsync(int paymentId, string referenceNo, string fileName, byte[] data)
    {
        if (string.IsNullOrWhiteSpace(referenceNo)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            var path = await gw.UploadAsync("payment-proofs", $"{uid}/{paymentId}-{fileName}", data);
            await gw.RpcAsync("submit_payment_proof", new() { ["p_payment_id"] = paymentId, ["p_reference_no"] = referenceNo.Trim(), ["p_proof_path"] = path });
            return true;
        });
    }

    public async Task<AppResult<Payment>> PayAgainAsync(PaymentItem item) => item.ItemId is not { } id
        ? AppResult<Payment>.Fail(AppErrorKind.Validation, "not_found")
        : item.Kind switch
        {
            PayKind.Membership => await AvailAsync(id),
            PayKind.Renewal => await RenewAsync(id),
            PayKind.Amenity => await Safe.RunAsync(() => gw.RpcAsync<Payment>("avail_amenity", new() { ["p_amenity_id"] = id })),
            _ => AppResult<Payment>.Fail(AppErrorKind.Validation, "not_found")
        };

    public Task<AppResult<GymSettings>> GymAsync() => Safe.RunAsync(async () => (await gw.ListAsync<GymSettings>()).First());

    public Task<AppResult<string?>> QrUrlAsync() => Safe.RunAsync(async () =>
    {
        var path = (await gw.ListAsync<GymSettings>()).FirstOrDefault()?.QrImagePath;
        return string.IsNullOrWhiteSpace(path) ? null : await gw.SignedUrlAsync("gym-assets", path);
    });
}

// ===================== Payment verification and the counter (staff) =====================
public sealed record PaymentRow(Payment Payment, string Payer, string Item, bool HasProof);
public sealed record CashRow(string Kind, long Payments, decimal Total);
/// <summary>Something the counter can take a payment for: a package, a membership to renew, or an amenity.</summary>
public sealed record CounterItem(int Id, string Label, decimal? Price = null);

public interface IPaymentStaffService
{
    Task<AppResult<IReadOnlyList<PaymentRow>>> PendingAsync();
    /// <summary>Verified payments, newest first. For finding a payment to void.</summary>
    Task<AppResult<IReadOnlyList<PaymentRow>>> RecentAsync();
    Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve, string? reason = null);
    /// <summary>Cash or GCash at the counter, verified at once. itemId is a package id, a membership id or an amenity id depending on kind.</summary>
    Task<AppResult<Payment>> RecordCounterAsync(string kind, string method, string? memberId, int? itemId, string? walkInName, string? referenceNo, string? discountCode);
    Task<AppResult<bool>> VoidAsync(int paymentId, string reason);
    Task<AppResult<IReadOnlyList<CashRow>>> CashSummaryAsync(DateOnly day);
    Task<AppResult<string?>> ProofUrlAsync(string proofPath);
    /// <summary>What can be sold for a kind of payment. Renewals are the member's own memberships, so they need a memberId.</summary>
    Task<AppResult<IReadOnlyList<CounterItem>>> ItemsAsync(string kind, string? memberId);
}

public sealed class PaymentStaffService(ISupabaseGateway gw) : IPaymentStaffService
{
    async Task<IReadOnlyList<PaymentRow>> RowsAsync(IEnumerable<Payment> payments)
    {
        var names = await Directory_.UserNamesAsync(gw);
        var look = await PaymentLookup.LoadAsync(gw);
        return payments.Select(p => new PaymentRow(p, p.WalkInName ?? names.GetValueOrDefault(p.MemberId ?? "", "Unknown"), look.Item(p), p.ProofPath is not null)).ToList();
    }

    public Task<AppResult<IReadOnlyList<PaymentRow>>> PendingAsync() => Safe.RunAsync(async () =>
        await RowsAsync((await gw.ListAsync<Payment>(p => p.Status == "pending")).OrderBy(p => p.CreatedAt)));

    public Task<AppResult<IReadOnlyList<PaymentRow>>> RecentAsync() => Safe.RunAsync(async () =>
        await RowsAsync((await gw.ListAsync<Payment>(p => p.Status == "verified")).OrderByDescending(p => p.VerifiedAt).Take(30)));

    public async Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve, string? reason = null)
    {
        if (!approve && string.IsNullOrWhiteSpace(reason)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () =>
        {
            var args = new Dictionary<string, object> { ["p_payment_id"] = paymentId, ["p_approve"] = approve };
            if (!approve) args["p_reason"] = reason!.Trim();
            await gw.RpcAsync("verify_payment", args);
            return true;
        });
    }

    public Task<AppResult<Payment>> RecordCounterAsync(string kind, string method, string? memberId, int? itemId, string? walkInName, string? referenceNo, string? discountCode) =>
        Safe.RunAsync(() =>
        {
            var args = new Dictionary<string, object> { ["p_kind"] = kind, ["p_method"] = method };
            if (memberId is not null) args["p_member_id"] = memberId;
            if (itemId is not null) args["p_item_id"] = itemId.Value;
            if (!string.IsNullOrWhiteSpace(walkInName)) args["p_walk_in_name"] = walkInName.Trim();
            if (!string.IsNullOrWhiteSpace(referenceNo)) args["p_reference_no"] = referenceNo.Trim();
            if (!string.IsNullOrWhiteSpace(discountCode)) args["p_discount_code"] = discountCode.Trim();
            return gw.RpcAsync<Payment>("record_counter_payment", args);
        });

    public async Task<AppResult<bool>> VoidAsync(int paymentId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () => { await gw.RpcAsync("void_payment", new() { ["p_payment_id"] = paymentId, ["p_reason"] = reason.Trim() }); return true; });
    }

    public Task<AppResult<IReadOnlyList<CashRow>>> CashSummaryAsync(DateOnly day) => Safe.RunAsync(async () =>
        (IReadOnlyList<CashRow>)(await gw.RpcAsync<List<CashRow>>("daily_cash_summary", new() { ["p_day"] = day.ToString("yyyy-MM-dd") }) ?? []));

    public Task<AppResult<string?>> ProofUrlAsync(string proofPath) => Safe.RunAsync(() => gw.SignedUrlAsync("payment-proofs", proofPath));

    public Task<AppResult<IReadOnlyList<CounterItem>>> ItemsAsync(string kind, string? memberId) => Safe.RunAsync<IReadOnlyList<CounterItem>>(async () =>
    {
        switch (kind)
        {
            case PayKind.Membership:
                return (await gw.ListAsync<MembershipPackage>(p => p.IsActive == true)).Select(p => new CounterItem(p.Id, p.Name, p.Price)).ToList();
            case PayKind.Amenity:
                return (await gw.ListAsync<Amenity>(a => a.IsActive == true)).Select(a => new CounterItem(a.Id, a.Name, a.Price)).ToList();
            case PayKind.Renewal:
                var packages = await gw.ListAsync<MembershipPackage>();
                return (await gw.ListAsync<UserMembershipPackage>(m => m.MemberId == memberId && (m.Status == "active" || m.Status == "expired")))
                    .Select(m =>
                    {
                        var package = packages.FirstOrDefault(p => p.Id == m.MembershipPackageId);
                        return new CounterItem(m.Id, $"{package?.Name ?? "Membership"}  ({m.Status}, ends {m.EndsAt:d MMM yyyy})", package?.Price);
                    }).ToList();
            default:
                return [];
        }
    });
}

// ===================== Amenities =====================
/// <summary>One amenity in the catalogue and where the member stands with it: not bought, paid and waiting, or active.</summary>
public sealed record AmenityRow(Amenity Amenity, int? UserAmenityId, string? Status = null, DateTime? ExpiresAt = null)
{
    public bool IsActive => Status == Models.Status.Active;
    public bool IsPending => Status == Models.Status.Pending;
    public bool CanBuy => Status is null or Models.Status.Expired or Models.Status.Cancelled;
}
public sealed record UsageRow(AmenityUsage Usage, string Member, string Amenity);

public sealed record AmenityCard(Amenity Amenity, bool Followed, bool CanFollow)
{
    public string Availability => Amenity.Availability;
    public bool IsUnavailable => Amenity.Availability == "unavailable";
    public string? Reason => Amenity.StatusReason;
    public DateTime? BackAt => Amenity.BackAt;
    public bool HasReason => !string.IsNullOrWhiteSpace(Amenity.StatusReason);
    public bool HasBackAt => IsUnavailable && Amenity.BackAt is not null;
    // empty hides the button: coaches and staff can look but not save
    public string FollowText => !CanFollow ? "" : Followed ? "Following" : "Notify me";

    /// <summary>Everything the card shows is the same, so a refresh can leave it alone.</summary>
    public bool SameAs(AmenityCard o) => Followed == o.Followed && CanFollow == o.CanFollow && Amenity.Name == o.Amenity.Name && Amenity.Description == o.Amenity.Description
        && Availability == o.Availability && Reason == o.Reason && BackAt == o.BackAt;
}

/// <summary>What a status change did: whether the amenity really changed, and the members who were alerted.</summary>
public sealed record AmenityChange(bool Changed, IReadOnlyList<NotifiedMember> Told);

public sealed record AmenityBoard(IReadOnlyList<AmenityCard> Cards, bool CanFollow);

public interface IAmenityService
{
    /// <summary>Every amenity that is switched on, with its live status and whether this member saved it. CanFollow is false for coaches and staff.</summary>
    Task<AppResult<AmenityBoard>> BoardAsync();
    Task<AppResult<bool>> SetFollowedAsync(int amenityId, bool follow);
    /// <summary>Front desk and admin only. Says whether anything really changed (judged against the amenity as it is now, not an older card) and who was alerted.</summary>
    Task<AppResult<AmenityChange>> SetStatusAsync(int amenityId, string status, string? reason, DateTime? backAtUtc);

    Task<AppResult<IReadOnlyList<AmenityRow>>> CatalogAsync();
    Task<AppResult<Payment>> AvailAsync(int amenityId, string? discountCode = null);
    Task<AppResult<bool>> LogUsageAsync(int userAmenityId, (string FileName, byte[] Data)? proof);
    Task<AppResult<IReadOnlyList<UsageRow>>> PendingUsagesAsync();
    Task<AppResult<bool>> VerifyUsageAsync(int usageId, bool approve);
}

public sealed class AmenityService(ISupabaseGateway gw) : IAmenityService
{
    static readonly string[] Statuses = ["available", "in_use", "unavailable"];

    public Task<AppResult<AmenityBoard>> BoardAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var amenities = (await gw.ListAsync<Amenity>(a => a.IsActive == true)).OrderBy(a => a.Name).ToList();
        var saved = (await gw.ListAsync<AmenityFollow>(f => f.MemberId == uid)).Select(f => f.AmenityId).ToHashSet();
        var isMember = (await gw.ListAsync<Member>(m => m.UserId == uid)).Count > 0;
        return new AmenityBoard(amenities.Select(a => new AmenityCard(a, saved.Contains(a.Id), isMember)).ToList(), isMember);
    });

    public Task<AppResult<bool>> SetFollowedAsync(int amenityId, bool follow) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("set_amenity_followed", new() { ["p_amenity_id"] = amenityId, ["p_follow"] = follow }); return true; });

    public async Task<AppResult<AmenityChange>> SetStatusAsync(int amenityId, string status, string? reason, DateTime? backAtUtc)
    {
        var trimmed = reason?.Trim();
        var problem = !Statuses.Contains(status) ? "bad_status" : trimmed?.Length > 200 ? "bad_reason" : backAtUtc <= DateTime.UtcNow ? "bad_back_at" : null;
        if (problem is not null) return AppResult<AmenityChange>.Fail(AppErrorKind.Validation, problem);

        var args = new Dictionary<string, object> { ["p_amenity_id"] = amenityId, ["p_status"] = status };
        if (!string.IsNullOrWhiteSpace(trimmed)) args["p_reason"] = trimmed;
        if (backAtUtc is { } back) args["p_back_at"] = back.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        return await Safe.RunAsync(async () =>
        {
            // The card on a front desk screen can be half a minute old, so ask what the amenity is right now. The same rule as the database
            // (a reason and a return time only count while unavailable) decides whether this save really changes anything.
            var current = (await gw.ListAsync<Amenity>(a => a.Id == amenityId)).FirstOrDefault();
            var unavailable = status == "unavailable";
            var wantReason = unavailable && !string.IsNullOrWhiteSpace(trimmed) ? trimmed : null;
            var wantBack = unavailable ? backAtUtc : null;
            var changed = current is null || current.Availability != status || current.StatusReason != wantReason || !SameMoment(current.BackAt, wantBack);
            var told = await gw.RpcAsync<List<NotifiedMember>>("set_amenity_status", args) ?? [];
            return new AmenityChange(changed, told);
        });
    }

    static bool SameMoment(DateTime? a, DateTime? b) => a is null ? b is null : b is not null && Math.Abs((a.Value.ToUniversalTime() - b.Value.ToUniversalTime()).TotalSeconds) < 1;

    public Task<AppResult<IReadOnlyList<AmenityRow>>> CatalogAsync() => Safe.RunAsync<IReadOnlyList<AmenityRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var now = DateTime.UtcNow;
        var catalog = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        var mine = await gw.ListAsync<UserAmenity>(a => a.MemberId == uid);
        // the latest entry per amenity tells where the member stands; an active one that ran out counts as expired
        return catalog.Select(a =>
        {
            var e = mine.Where(m => m.AmenityId == a.Id).OrderByDescending(m => m.AvailedAt).ThenByDescending(m => m.Id).FirstOrDefault();
            var status = e is null ? null : e.Status == Status.Active && e.ExpiresAt <= now ? Status.Expired : e.Status;
            return new AmenityRow(a, e?.Id, status, e?.ExpiresAt);
        }).ToList();
    });

    public Task<AppResult<Payment>> AvailAsync(int amenityId, string? discountCode = null)
    {
        var args = new Dictionary<string, object> { ["p_amenity_id"] = amenityId };
        if (!string.IsNullOrWhiteSpace(discountCode)) args["p_discount_code"] = discountCode.Trim();
        return Safe.RunAsync(() => gw.RpcAsync<Payment>("avail_amenity", args));
    }

    public Task<AppResult<bool>> LogUsageAsync(int userAmenityId, (string FileName, byte[] Data)? proof) => Safe.RunAsync(async () =>
    {
        string? path = null;
        if (proof is { } p)
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            path = await gw.UploadAsync("amenity-proofs", $"{uid}/{userAmenityId}-{p.FileName}", p.Data);
        }
        await gw.InsertAsync(new AmenityUsage { UserAmenityId = userAmenityId, ProofPath = path });
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
            return new UsageRow(u, names.GetValueOrDefault(e?.MemberId ?? "", "Unknown"), amenities.FirstOrDefault(a => a.Id == e?.AmenityId)?.Name ?? "Amenity");
        }).ToList();
    });

    public Task<AppResult<bool>> VerifyUsageAsync(int usageId, bool approve) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("verify_amenity_usage", new() { ["p_usage_id"] = usageId, ["p_approve"] = approve });
        return true;
    });
}

// ===================== Coaches =====================
public sealed record CoachCard(Coach Coach, string Name, bool Hired, int? HireId, IReadOnlyList<ShiftPost>? Shifts = null)
{
    /// <summary>The coach paused hiring and this member has no hire to keep: shown grayed out, Hire disabled.</summary>
    public bool Closed => !Coach.IsAvailable && !Hired;

    public string ShiftsText => Shifts is { Count: > 0 }
        ? string.Join("\n", Shifts.Take(3).Select(s => $"{Fmt.Range(s.Start, s.End)}{(string.IsNullOrWhiteSpace(s.Note) ? "" : "  " + s.Note)}"))
        : "No shifts posted yet.";
}

public sealed record TraineeRow(int HireId, string CoachId, string MemberId, string Name, string NextText, int Completed);

public interface ICoachService
{
    Task<AppResult<IReadOnlyList<CoachCard>>> ListCoachesAsync();
    Task<AppResult<bool>> HireAsync(string coachId);
    Task<AppResult<bool>> EndHireAsync(int hireId);
    Task<AppResult<Coach?>> MyCoachAsync();
    Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, decimal? rate, bool available);
    Task<AppResult<IReadOnlyList<TraineeRow>>> TraineesAsync();
    Task<AppResult<IReadOnlyList<ShiftPost>>> MyShiftsAsync();
    Task<AppResult<bool>> PostShiftAsync(DateTime startUtc, DateTime endUtc, string? note);
    Task<AppResult<bool>> CancelShiftAsync(int shiftId);
}

public sealed class CoachService(ISupabaseGateway gw) : ICoachService
{
    public Task<AppResult<IReadOnlyList<CoachCard>>> ListCoachesAsync() => Safe.RunAsync<IReadOnlyList<CoachCard>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var coaches = await gw.ListAsync<Coach>();   // paused coaches stay listed, shown as closed
        var names = await Directory_.UserNamesAsync(gw);
        var hires = await gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "active");
        var now = DateTime.UtcNow;
        var shifts = await gw.ListAsync<ShiftPost>(s => s.Status == "scheduled" && s.End > now);
        return coaches.Select(c => new CoachCard(c, names.GetValueOrDefault(c.UserId, "Coach"),
            hires.Any(h => h.CoachId == c.UserId), hires.FirstOrDefault(h => h.CoachId == c.UserId)?.Id,
            shifts.Where(s => s.CoachId == c.UserId).OrderBy(s => s.Start).ToList())).ToList();
    });

    public Task<AppResult<bool>> HireAsync(string coachId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync<int>("hire_coach", new() { ["p_coach_id"] = coachId }); return true; });

    public Task<AppResult<bool>> EndHireAsync(int hireId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("end_hire", new() { ["p_hire_id"] = hireId }); return true; });

    public Task<AppResult<Coach?>> MyCoachAsync() => Safe.RunAsync<Coach?>(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault(); });

    public Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, decimal? rate, bool available) => Safe.RunAsync(async () =>
    {
        var c = (await MyCoachAsync()).Value ?? throw new Exception("not_found");
        c.Bio = bio; c.Specialty = specialty; c.HourlyRate = rate; c.IsAvailable = available;
        await gw.UpdateAsync(c);
        return true;
    });

    public Task<AppResult<IReadOnlyList<TraineeRow>>> TraineesAsync() => Safe.RunAsync<IReadOnlyList<TraineeRow>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var hires = await gw.ListAsync<CoachHire>(h => h.CoachId == uid && h.Status == "active");
        var names = await Directory_.UserNamesAsync(gw);
        var sessions = await gw.ListAsync<TrainingSession>(s => s.CoachId == uid);
        var now = DateTime.UtcNow;
        return hires.Select(h =>
        {
            var mine = sessions.Where(s => s.MemberId == h.MemberId).ToList();
            var next = mine.Where(s => s.Status == Status.Scheduled && s.ScheduledStart > now).OrderBy(s => s.ScheduledStart).FirstOrDefault();
            return new TraineeRow(h.Id, uid, h.MemberId, names.GetValueOrDefault(h.MemberId, "Member"),
                next is null ? "No session booked" : $"Next: {Fmt.Range(next.ScheduledStart, next.ScheduledEnd)}",
                mine.Count(s => s.Status == Status.Completed));
        }).OrderBy(t => t.Name).ToList();
    });

    public Task<AppResult<IReadOnlyList<ShiftPost>>> MyShiftsAsync() => Safe.RunAsync<IReadOnlyList<ShiftPost>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var now = DateTime.UtcNow;
        return (await gw.ListAsync<ShiftPost>(s => s.CoachId == uid && s.End > now && s.Status == "scheduled")).OrderBy(s => s.Start).ToList();
    });

    public async Task<AppResult<bool>> PostShiftAsync(DateTime startUtc, DateTime endUtc, string? note)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            await gw.InsertAsync(new ShiftPost { CoachId = uid, Start = startUtc, End = endUtc, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
            return true;
        });
    }

    public Task<AppResult<bool>> CancelShiftAsync(int shiftId) => Safe.RunAsync(async () =>
    {
        var row = (await gw.ListAsync<ShiftPost>(s => s.Id == shiftId)).First();
        row.Status = Status.Cancelled;
        await gw.UpdateAsync(row);
        return true;
    });
}

// ===================== Time requests and sessions =====================
public sealed record RequestRow(TimeRequest Request, string With, string Kind, bool CanRespond, bool CanCancel);
public sealed record SessionRow(TrainingSession Session, string With, bool IsMine, bool CanManage, bool CanChange);
public sealed record RequestsView(IReadOnlyList<RequestRow> Rows, bool IsCoach, IReadOnlyList<CoachCard> HiredCoaches);

public interface ISessionService
{
    Task<AppResult<RequestsView>> RequestsAsync();
    Task<AppResult<bool>> RequestAsync(string coachId, DateTime startUtc, DateTime endUtc, string? message);
    Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc);
    Task<AppResult<bool>> RespondAsync(int requestId, bool approve);
    Task<AppResult<bool>> CancelRequestAsync(int requestId);
    Task<AppResult<IReadOnlyList<SessionRow>>> SessionsAsync();
    /// <summary>The coach marks a session done (Completed) or missed (NoShow). Recorded as attendance, which also sets the status.</summary>
    Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status);
    Task<AppResult<bool>> RescheduleAsync(int sessionId, DateTime startUtc, DateTime endUtc);
    Task<AppResult<bool>> CancelSessionAsync(int sessionId);
    /// <summary>The signed-in coach's sessions that start in [fromUtc, toUtc), oldest first, each with how it went (Mark is null until marked).</summary>
    Task<AppResult<IReadOnlyList<CalendarSession>>> CalendarAsync(DateTime fromUtc, DateTime toUtc);
    /// <summary>The signed-in coach's scheduled sessions from now on, nearest first.</summary>
    Task<AppResult<IReadOnlyList<CalendarSession>>> UpcomingAsync();
}

public sealed class SessionService(ISupabaseGateway gw) : ISessionService
{
    public Task<AppResult<RequestsView>> RequestsAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var coaches = await gw.ListAsync<Coach>();
        var iAmCoach = coaches.Any(c => c.UserId == uid);
        var names = await Directory_.UserNamesAsync(gw);
        var requests = await gw.ListAsync<TimeRequest>(r => r.Status == "pending");

        string CoachName(string id) => names.GetValueOrDefault(id, "Coach");
        string MemberName(string? id) => id is null ? "Open slot" : names.GetValueOrDefault(id, "Member");

        var rows = requests.OrderBy(r => r.RequestedStart).Select(r =>
        {
            var iAmCoachOfThis = r.CoachId == uid;
            var mine = (r.RequestedBy == "member" && r.MemberId == uid) || (r.RequestedBy == "coach" && iAmCoachOfThis);
            var canRespond = !mine && (r.RequestedBy == "member" ? iAmCoachOfThis : !iAmCoachOfThis);
            var kind = r.SessionId is not null ? (r.RequestedBy == "member" ? "Reschedule asked by member" : "Reschedule asked by coach")
                : r.RequestedBy == "member" ? "Requested by member" : r.MemberId is null ? "Open availability" : "Offered by coach";
            return new RequestRow(r, iAmCoachOfThis ? MemberName(r.MemberId) : CoachName(r.CoachId), kind, canRespond, mine);
        }).ToList();

        var hires = await gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "active");
        IReadOnlyList<CoachCard> hired = coaches.Where(c => hires.Any(h => h.CoachId == c.UserId))
            .Select(c => new CoachCard(c, CoachName(c.UserId), true, null)).ToList();
        return new RequestsView(rows, iAmCoach, hired);
    });

    public async Task<AppResult<bool>> RequestAsync(string coachId, DateTime startUtc, DateTime endUtc, string? message)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () => { await gw.RpcAsync<int>("request_time", TimeArgs(coachId, startUtc, endUtc, message)); return true; });
    }

    public async Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
            await gw.RpcAsync<int>("request_time", TimeArgs(uid, startUtc, endUtc, null));   // a coach asking for their own id is an open slot
            return true;
        });
    }

    static Dictionary<string, object> TimeArgs(string coachId, DateTime start, DateTime end, string? message)
    {
        var args = new Dictionary<string, object> { ["p_coach_id"] = coachId, ["p_start"] = start.ToString("O"), ["p_end"] = end.ToString("O") };
        if (!string.IsNullOrWhiteSpace(message)) args["p_message"] = message.Trim();
        return args;
    }

    public Task<AppResult<bool>> RespondAsync(int requestId, bool approve) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("respond_time_request", new() { ["p_request_id"] = requestId, ["p_approve"] = approve });
        return true;
    });

    public Task<AppResult<bool>> CancelRequestAsync(int requestId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("cancel_time_request", new() { ["p_request_id"] = requestId }); return true; });

    public Task<AppResult<IReadOnlyList<SessionRow>>> SessionsAsync() => Safe.RunAsync<IReadOnlyList<SessionRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var names = await Directory_.UserNamesAsync(gw);
        var sessions = await gw.ListAsync<TrainingSession>();
        return sessions.OrderBy(s => s.ScheduledStart).Select(s =>
        {
            var isMine = s.CoachId == uid;
            var with = isMine ? s.MemberId : s.CoachId;
            return new SessionRow(s, names.GetValueOrDefault(with, "Unknown"), isMine, isMine && s.Status == Status.Scheduled, s.Status == Status.Scheduled);
        }).ToList();
    });

    public async Task<AppResult<bool>> RescheduleAsync(int sessionId, DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            await gw.RpcAsync<int>("request_reschedule", new() { ["p_session_id"] = sessionId, ["p_start"] = startUtc.ToString("O"), ["p_end"] = endUtc.ToString("O") });
            return true;
        });
    }

    public Task<AppResult<bool>> CancelSessionAsync(int sessionId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("cancel_session", new() { ["p_session_id"] = sessionId }); return true; });

    public Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("record_session_attendance", new() { ["p_session_id"] = sessionId, ["p_status"] = status == Status.NoShow ? "absent" : "present" });
        return true;
    });

    public Task<AppResult<IReadOnlyList<CalendarSession>>> CalendarAsync(DateTime fromUtc, DateTime toUtc) => Safe.RunAsync<IReadOnlyList<CalendarSession>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var sessions = await gw.ListAsync<TrainingSession>(s => s.CoachId == uid && s.ScheduledStart >= fromUtc && s.ScheduledStart < toUtc);
        var ids = sessions.Select(s => s.Id).ToList();
        var marks = (await gw.ListAsync<SessionAttendance>(a => ids.Contains(a.SessionId))).ToDictionary(a => a.SessionId, a => a.Status);   // asked even for an empty month: one tiny request, and the filter stays proven
        var names = await Directory_.UserNamesAsync(gw);
        return sessions.OrderBy(s => s.ScheduledStart)
            .Select(s => new CalendarSession(s, names.GetValueOrDefault(s.MemberId, "Member"), marks.GetValueOrDefault(s.Id))).ToList();
    });

    public Task<AppResult<IReadOnlyList<CalendarSession>>> UpcomingAsync() => Safe.RunAsync<IReadOnlyList<CalendarSession>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var now = DateTime.UtcNow;
        var sessions = await gw.ListAsync<TrainingSession>(s => s.CoachId == uid && s.Status == Status.Scheduled && s.ScheduledStart >= now);
        var names = await Directory_.UserNamesAsync(gw);
        return sessions.OrderBy(s => s.ScheduledStart).Select(s => new CalendarSession(s, names.GetValueOrDefault(s.MemberId, "Member"), null)).ToList();
    });
}

// ===================== Attendance and check-ins =====================
public sealed record MemberHit(string MemberId, string Username, bool HasActiveMembership)
{
    public string PlanStatus => HasActiveMembership ? "active" : "inactive";
}

public interface IAttendanceService
{
    /// <summary>The coach records how a member did at a session: present, late, absent or excused.</summary>
    Task<AppResult<bool>> RecordAsync(int sessionId, string status);
    Task<AppResult<bool>> CheckInAsync(string memberId);
    Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string usernameFragment);
}

public sealed class AttendanceService(ISupabaseGateway gw) : IAttendanceService
{
    public Task<AppResult<bool>> RecordAsync(int sessionId, string status) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("record_session_attendance", new() { ["p_session_id"] = sessionId, ["p_status"] = status });
        return true;
    });

    public Task<AppResult<bool>> CheckInAsync(string memberId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync<int>("record_check_in", new() { ["p_member_id"] = memberId }); return true; });

    public Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string fragment) => Safe.RunAsync<IReadOnlyList<MemberHit>>(async () =>
    {
        var members = await gw.ListAsync<Member>();
        var profiles = await gw.ListAsync<Profile>();
        var now = DateTime.UtcNow;
        var active = (await gw.ListAsync<UserMembershipPackage>(m => m.Status == "active")).Where(m => m.EndsAt > now).Select(m => m.MemberId).ToHashSet();
        return members.Join(profiles, m => m.UserId, p => p.Id, (m, p) => new MemberHit(m.UserId, string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName, active.Contains(m.UserId)))
            .Where(h => h.Username.Contains(fragment, StringComparison.OrdinalIgnoreCase)).Take(20).ToList();
    });
}

// ===================== Notifications =====================
public interface INotificationService
{
    /// <summary>Raised after an alert is read or all are, so the bell and an open panel redraw together.</summary>
    event Action? Changed;
    /// <summary>Newest first.</summary>
    Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync();
    Task<AppResult<int>> UnreadCountAsync();
    Task<AppResult<bool>> MarkReadAsync(int id);
    Task<AppResult<bool>> MarkAllReadAsync();
    /// <summary>Something outside this service cleared alerts (a chat was opened): redraw the bell.</summary>
    void Refresh();
}

public sealed class NotificationService(ISupabaseGateway gw) : INotificationService
{
    public event Action? Changed;

    public void Refresh() => Changed?.Invoke();

    public Task<AppResult<IReadOnlyList<UserNotification>>> ListAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        return (IReadOnlyList<UserNotification>)(await gw.ListAsync<UserNotification>(n => n.UserId == uid)).OrderByDescending(n => n.CreatedAt).ToList();
    });

    // fetches only the unread rows, so the bell does not download every alert you ever had
    public Task<AppResult<int>> UnreadCountAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        return (await gw.ListAsync<UserNotification>(n => n.UserId == uid && n.ReadAt == null)).Count;
    });

    public Task<AppResult<bool>> MarkReadAsync(int id) => Safe.RunAsync(async () =>
    {
        var row = (await gw.ListAsync<UserNotification>(n => n.Id == id)).First();
        row.ReadAt = DateTime.UtcNow;
        await gw.UpdateAsync(row);
        Changed?.Invoke();
        return true;
    });

    public Task<AppResult<bool>> MarkAllReadAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        await gw.UpdateWhereAsync<UserNotification, DateTime?>(n => n.UserId == uid && n.ReadAt == null, n => n.ReadAt, DateTime.UtcNow);
        Changed?.Invoke();
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
public sealed record DiscountRow(Discount Discount, string State);

public interface IAdminService
{
    Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync();
    Task<AppResult<bool>> AssignRoleAsync(string userId, string role);
    Task<AppResult<bool>> RevokeRoleAsync(string userId, string role);
    Task<AppResult<bool>> SetActiveAsync(string userId, bool active);
    Task<AppResult<bool>> CreateUserAsync(string name, string email, string role, string password);
    Task<AppResult<bool>> ResetPasswordAsync(string userId, string password);
    Task<AppResult<bool>> DeletePackageAsync(MembershipPackage package);
    Task<AppResult<IReadOnlyList<DiscountRow>>> DiscountsAsync();
    Task<AppResult<bool>> SaveDiscountAsync(Discount discount);
    Task<AppResult<bool>> DeleteDiscountAsync(Discount discount);
    Task<AppResult<bool>> SavePackageAsync(MembershipPackage package);
    Task<AppResult<bool>> SaveAmenityAsync(Amenity amenity);
    Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync();
    Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync();
    Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to);
    Task<AppResult<IReadOnlyList<AuditRow>>> AuditAsync();
    Task<AppResult<GymSettings>> GymSettingsAsync();
    /// <summary>Saves the gym name, QR account name and walk-in price, and uploads a new payment QR image when one is given.</summary>
    Task<AppResult<bool>> SaveGymSettingsAsync(GymSettings settings, (string FileName, byte[] Data)? qrImage);
}

public sealed class AdminService(ISupabaseGateway gw) : IAdminService
{
    public Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync() => Safe.RunAsync<IReadOnlyList<UserRow>>(async () =>
    {
        var profiles = await gw.ListAsync<Profile>();
        var roles = await gw.ListAsync<Role>();
        var links = await gw.ListAsync<UserRole>();
        return profiles.Select(p => new UserRow(p.Id, string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName, p.IsActive,
            links.Where(l => l.UserId == p.Id).Select(l => roles.First(r => r.RoleId == l.RoleId).Name).OrderBy(n => n).ToList())).ToList();
    });

    public Task<AppResult<bool>> AssignRoleAsync(string userId, string role) => Rpc("assign_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> RevokeRoleAsync(string userId, string role) => Rpc("revoke_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> SetActiveAsync(string userId, bool active) => Rpc("set_user_active", new() { ["p_user"] = userId, ["p_active"] = active });

    // accounts are made by an Edge Function: it holds the service-role key, which the app never has
    public async Task<AppResult<bool>> CreateUserAsync(string name, string email, string role, string password)
    {
        if (string.IsNullOrWhiteSpace(name) || !email.Contains('@') || password.Length < 6 || role is not ("member" or "coach" or "employee"))
            return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        if (!AccountRules.IsName(name.Trim())) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name");   // the database refuses it too (0012)
        return await Safe.RunAsync(async () =>
        {
            await gw.InvokeFunctionAsync("create-user", new() { ["name"] = name.Trim(), ["email"] = email.Trim(), ["role"] = role, ["password"] = password });
            return true;
        });
    }

    public async Task<AppResult<bool>> ResetPasswordAsync(string userId, string password)
    {
        if (password.Length < 6) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () => { await gw.InvokeFunctionAsync("reset-password", new() { ["user_id"] = userId, ["password"] = password }); return true; });
    }

    public Task<AppResult<bool>> DeletePackageAsync(MembershipPackage p) => Safe.RunAsync(async () => { await gw.DeleteAsync(p); return true; });

    public Task<AppResult<IReadOnlyList<DiscountRow>>> DiscountsAsync() => Safe.RunAsync<IReadOnlyList<DiscountRow>>(async () =>
    {
        var now = DateTime.UtcNow;
        return (await gw.ListAsync<Discount>()).OrderByDescending(d => d.ExpiresAt)
            .Select(d => new DiscountRow(d, !d.IsActive ? "inactive" : d.ExpiresAt <= now ? "expired" : "active")).ToList();
    });

    public async Task<AppResult<bool>> SaveDiscountAsync(Discount d)
    {
        if (string.IsNullOrWhiteSpace(d.Code) || d.Percent is < 1 or > 100) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_discount");
        d.Code = d.Code.Trim().ToUpperInvariant();
        return await Safe.RunAsync(async () => { if (d.Id == 0) await gw.InsertAsync(d); else await gw.UpdateAsync(d); return true; });
    }

    public Task<AppResult<bool>> DeleteDiscountAsync(Discount d) => Safe.RunAsync(async () => { await gw.DeleteAsync(d); return true; });

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

    public Task<AppResult<GymSettings>> GymSettingsAsync() => Safe.RunAsync(async () => (await gw.ListAsync<GymSettings>()).First());

    public async Task<AppResult<bool>> SaveGymSettingsAsync(GymSettings s, (string FileName, byte[] Data)? qrImage)
    {
        if (string.IsNullOrWhiteSpace(s.GymName) || s.WalkInPrice < 0) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () =>
        {
            if (qrImage is { } img) s.QrImagePath = await gw.UploadAsync("gym-assets", $"qr-{DateTime.UtcNow:yyyyMMddHHmmss}-{img.FileName}", img.Data);
            await gw.UpdateAsync(s);
            return true;
        });
    }

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
