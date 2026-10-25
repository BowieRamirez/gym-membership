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
    Task<AppResult<bool>> RegisterAsync(string firstName, string lastName, string email, string password);
    Task<AppResult<EmailStatus>> EmailStatusAsync();
    /// <summary>Emails a 6-digit code to this address. A different address first becomes your sign-in email.</summary>
    Task<AppResult<bool>> SendEmailCodeAsync(string email);
    Task<AppResult<bool>> ConfirmEmailAsync(string email, string code);
    /// <summary>Forgotten password: emails a code if an account uses this address. The answer is the same either way.</summary>
    Task<AppResult<bool>> SendResetCodeAsync(string email);
    /// <summary>Checks the code, sets the new password, then signs out so the person signs in with it.</summary>
    Task<AppResult<bool>> ResetPasswordAsync(string email, string code, string newPassword);
}

/// <summary>What a new account may use. The entry caps on the register page read the same numbers.</summary>
public static partial class AccountRules
{
    public const int MinName = 2, MaxName = 60, MaxEmailLocal = 64, MaxEmail = 100, MinPassword = 6, MaxPassword = 64;

    // letters (any script), joined by single spaces, hyphens or apostrophes: "Mary-Ann O'Brien", "Juan dela Cruz"
    [GeneratedRegex(@"^[\p{L}\p{M}]+(?:[ '\-][\p{L}\p{M}]+)*$")] private static partial Regex NameShape();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailShape();

    public static bool IsName(string name) => name.Length is >= MinName and <= MaxName && NameShape().IsMatch(name);

    /// <summary>Both parts are names, and "first last" fits the 60 characters the database allows for the full name.</summary>
    public static bool IsName(string first, string last) => IsName(first) && IsName(last) && NameParts.Join(first, last).Length <= MaxName;

    /// <summary>A code from the email: exactly six digits.</summary>
    public static bool IsCode(string code) => code.Length == 6 && code.All(char.IsAsciiDigit);

    public static bool IsEmail(string email) =>
        email.Length <= MaxEmail && EmailShape().IsMatch(email) && email.IndexOf('@') <= MaxEmailLocal;

    public static bool IsPassword(string password) => password.Length is >= MinPassword and <= MaxPassword;

    /// <summary>What the name field keeps as you type: letters (and their accents), spaces, hyphens and apostrophes.</summary>
    public static string KeepNameChars(string typed) => new(typed.Where(c =>
        char.IsLetter(c) || c is ' ' or '\'' or '-' ||
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark).ToArray());
}

/// <summary>First and last name, as the database keeps them (0035): the full name is "first last"; an old name splits at its first space.</summary>
public static class NameParts
{
    public static string Join(string first, string last) => $"{first.Trim()} {last.Trim()}".Trim();

    public static (string First, string Last) Split(string full)
    {
        var name = full.Trim();
        var space = name.IndexOf(' ');
        return space < 0 ? (name, "") : (name[..space], name[(space + 1)..].Trim());
    }
}

/// <summary>Your sign-in email, and whether you proved it with a code since it last changed.</summary>
public sealed record EmailStatus(string Email, bool Confirmed);

public sealed class AuthService(ISupabaseGateway gw) : IAuthService
{
    public string? UserId => gw.CurrentUserId;

    public async Task<AppResult<bool>> RegisterAsync(string firstName, string lastName, string email, string password)
    {
        firstName = firstName.Trim(); lastName = lastName.Trim(); email = email.Trim();
        if (!AccountRules.IsName(firstName, lastName)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name");
        if (!AccountRules.IsEmail(email)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_email");
        if (!AccountRules.IsPassword(password)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_password");
        return await Safe.RunAsync(async () => { await gw.SignUpAsync(email, password, firstName, lastName); return true; });
    }

    public Task<AppResult<EmailStatus>> EmailStatusAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var email = gw.CurrentEmail ?? throw new Exception("not authenticated");
        var confirmed = (await gw.ListAsync<Profile>(p => p.Id == uid)).FirstOrDefault()?.ConfirmedEmail;
        return new EmailStatus(email, string.Equals(confirmed, email, StringComparison.OrdinalIgnoreCase));
    });

    public async Task<AppResult<bool>> SendEmailCodeAsync(string email)
    {
        email = email.Trim();
        if (!AccountRules.IsEmail(email)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_email");
        return await Safe.RunAsync(async () => { await gw.SendEmailCodeAsync(email); return true; });
    }

    public async Task<AppResult<bool>> ConfirmEmailAsync(string email, string code)
    {
        code = code.Trim();
        if (!AccountRules.IsCode(code)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_code");
        return await Safe.RunAsync(async () =>
        {
            await gw.VerifyEmailCodeAsync(email.Trim(), code);
            await gw.RpcAsync("confirm_my_email");
            return true;
        });
    }

    public async Task<AppResult<bool>> SendResetCodeAsync(string email)
    {
        email = email.Trim();
        if (!AccountRules.IsEmail(email)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_email");
        return await Safe.RunAsync(async () => { await gw.SendPasswordResetCodeAsync(email); return true; });
    }

    public async Task<AppResult<bool>> ResetPasswordAsync(string email, string code, string newPassword)
    {
        code = code.Trim();
        if (!AccountRules.IsCode(code)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_code");
        if (!AccountRules.IsPassword(newPassword)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_password");
        return await Safe.RunAsync(async () =>
        {
            try
            {
                await gw.ResetPasswordWithCodeAsync(email.Trim(), code, newPassword);
                await gw.RpcAsync("mark_password_changed");   // they chose this one, so no temporary-password prompt
                await gw.RpcAsync("confirm_my_email");        // the code came to this inbox, which proves it
            }
            finally { if (gw.CurrentUserId is not null) await gw.SignOutAsync(); }
            return true;
        });
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
        PayKind.Membership => p.PackageSnapshotName ?? (p.UserMembershipPackageId is { } u ? PackageName(u) : "Membership"),
        PayKind.Renewal => "Extension: " + (p.PackageSnapshotName ?? (_renewals.FirstOrDefault(r => r.Id == p.RenewalId) is { } r ? PackageName(r.UserMembershipPackageId) : "Membership")),
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
    DateTime CreatedAt, DateTime? ExpiresAt, string? ReferenceNo, bool HasProof, string? ReceiptNo, string? RejectedReason, int? ItemId, int? PackageSnapshotId = null)
{
    /// <summary>A QR payment that still needs the member to pay, then send the reference number and screenshot.</summary>
    public bool NeedsProof => Status == Models.Status.Pending && Method == PayMethod.Qr && !HasProof && (ExpiresAt is null || ExpiresAt > DateTime.UtcNow);
    public bool AwaitingReview => Status == Models.Status.Pending && HasProof;
    /// <summary>A membership or extension payment nobody has verified yet: the member can still withdraw it.</summary>
    public bool CanCancel => Status == Models.Status.Pending && Kind is PayKind.Membership or PayKind.Renewal;
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
        var active = await gw.RpcAsync<bool>("is_active_member");
        var pays = await gw.ListAsync<Payment>(p => p.MemberId == uid);
        return new Access(active, pays.Any(p => p.Status == Status.Pending));
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
        var terms = await gw.ListAsync<MembershipTerm>(t => t.MemberId == uid);
        var now = DateTime.UtcNow;
        return terms.Where(t => t.CancelledAt is null && t.StartsAt <= now).OrderByDescending(t => t.EndsAt)
            .Select(t => new MembershipRow(t.MembershipId,t.PackageName,t.EndsAt > now ? Status.Active : Status.Expired,
                t.EndsAt,Math.Max(0,(int)Math.Ceiling((t.EndsAt-now).TotalDays)),t.DurationDays,false,t.AmountPaid)).ToList();
    });

    public Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync() => Safe.RunAsync<IReadOnlyList<PaymentItem>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var pays = await gw.ListAsync<Payment>(p => p.MemberId == uid);
        var look = await PaymentLookup.LoadAsync(gw);
        return pays.OrderByDescending(p => p.CreatedAt).Select(p => new PaymentItem(p.Id,
            p.DiscountAmount > 0 ? look.Item(p) + " (discount)" : look.Item(p), p.PaymentCode, p.Kind, p.Method, p.Amount, p.DiscountAmount, p.Status,
            p.CreatedAt, p.ExpiresAt, p.ReferenceNo, p.ProofPath is not null, p.ReceiptNo, p.RejectedReason, look.ItemId(p), p.PackageSnapshotId)).ToList();
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

    public async Task<AppResult<Payment>> PayAgainAsync(PaymentItem item) => item.PackageSnapshotId is { } package && item.Kind is PayKind.Membership or PayKind.Renewal
        ? await new SubscriptionService(gw).ExtendAsync(package)
        : item.ItemId is not { } id
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

public sealed record AmenityCard(Amenity Amenity, bool Followed, bool CanFollow, string? PhotoUrl = null, string? PassStatus = null, DateTime? PassUntil = null)
{
    // members only (CanFollow): what a pass costs, or where theirs stands; empty hides it for coaches and staff
    public string PassText => !CanFollow ? "" : PassStatus switch
    {
        Models.Status.Pending => "Waiting for your payment to be verified",
        Models.Status.Active => PassUntil is { } until ? $"Pass active until {until.ToLocalTime():d MMM yyyy}" : "Pass active",
        _ => Amenity.DurationDays is { } days ? $"₱{Amenity.Price:N2} for {days} days" : $"₱{Amenity.Price:N2}"
    };
    public string BuyText => CanFollow && PassStatus is not (Models.Status.Pending or Models.Status.Active) ? "Buy pass" : "";
    public bool CanBuy => BuyText.Length > 0;

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
        && Availability == o.Availability && Reason == o.Reason && BackAt == o.BackAt
        && Amenity.ImagePath == o.Amenity.ImagePath && PhotoUrl == o.PhotoUrl && PassText == o.PassText && BuyText == o.BuyText;
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
    readonly LinkCache photos = new(gw, "gym-assets", rememberMisses: true);

    public Task<AppResult<AmenityBoard>> BoardAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var amenities = (await gw.ListAsync<Amenity>(a => a.IsActive == true)).OrderBy(a => a.Name).ToList();
        var saved = (await gw.ListAsync<AmenityFollow>(f => f.MemberId == uid)).Select(f => f.AmenityId).ToHashSet();
        var isMember = (await gw.ListAsync<Member>(m => m.UserId == uid)).Count > 0;
        var passes = isMember ? (await CatalogAsync()).Value?.ToDictionary(r => r.Amenity.Id) ?? [] : [];
        var cards = await Task.WhenAll(amenities.Select(async a =>
            new AmenityCard(a, saved.Contains(a.Id), isMember, (await Safe.RunAsync(() => photos.UrlAsync(a.ImagePath))).Value,
                passes.GetValueOrDefault(a.Id)?.Status, passes.GetValueOrDefault(a.Id)?.ExpiresAt)));
        return new AmenityBoard(cards, isMember);
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
/// <summary>An hourly rate is a peso amount above 0 with at most two decimals. Never parsed or shown with the device's own separators.</summary>
public static partial class CoachRate
{
    [GeneratedRegex(@"^\d{1,10}([.,]\d{1,2})?$")] private static partial Regex Shape();

    /// <summary>"350", "350.5", "350,50" are fine; "0", "-1", "350.505", "1,000.00" are not.</summary>
    public static bool TryParse(string? text, out decimal rate)
    {
        rate = 0;
        var t = text?.Trim() ?? "";
        return Shape().IsMatch(t) && decimal.TryParse(t.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate) && rate > 0;
    }

    public static bool IsValid(decimal rate) => rate > 0 && decimal.Round(rate, 2) == rate;
    public static string Input(decimal? rate) => rate is > 0 ? rate.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";
    public static string Text(decimal? rate) => rate is > 0 ? $"₱{rate.Value.ToString("N2", CultureInfo.InvariantCulture)} per hour" : "Rate not set";
}

/// <summary>InGym: the coach has an open physical visit (0032). The list shows only coaches in the gym, plus your own coach.</summary>
public sealed record CoachCard(Coach Coach, string Name, bool Hired, int? HireId, IReadOnlyList<ShiftPost>? Shifts = null, string? PhotoUrl = null, bool InGym = true)
{
    public string Letters => Initials.From(Name);
    /// <summary>The coach paused hiring and this member has no hire to keep: shown grayed out, Hire disabled.</summary>
    public bool Closed => !Coach.IsAvailable && !Hired;
    public string RateText => CoachRate.Text(Coach.HourlyRate);

    public bool SameAs(CoachCard o) => Name == o.Name && Hired == o.Hired && HireId == o.HireId && InGym == o.InGym && PhotoUrl == o.PhotoUrl && ShiftsText == o.ShiftsText
        && Coach.IsAvailable == o.Coach.IsAvailable && Coach.Bio == o.Coach.Bio && Coach.Specialty == o.Coach.Specialty && Coach.HourlyRate == o.Coach.HourlyRate;

    public string ShiftsText => Shifts is { Count: > 0 }
        ? string.Join("\n", Shifts.Take(3).Select(s => $"{Fmt.Range(s.Start, s.End)}{(string.IsNullOrWhiteSpace(s.Note) ? "" : "  " + s.Note)}"))
        : "No shifts posted yet.";
}

public sealed record TraineeRow(int HireId, string CoachId, string MemberId, string Name, string NextText, int Completed);

/// <summary>A coach on the front desk's check-in page. InSince is null while they are not in the gym.</summary>
public sealed record CoachPresenceRow(string CoachId, string Name, DateTime? InSince)
{
    public bool InGym => InSince is not null;
    public string StateText => InSince is { } t ? $"In the gym since {Fmt.Time(t)}" : "Not in the gym";
    public string ActionText => InGym ? "Check out" : "Check in";
}

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
    /// <summary>The signed-in coach's open visit, or null when they are not checked in.</summary>
    Task<AppResult<CoachVisit?>> MyVisitAsync();
    /// <summary>Null coachId: the signed-in coach themselves. Otherwise front desk for that coach. Twice in a row is harmless.</summary>
    Task<AppResult<bool>> CheckInAsync(string? coachId = null);
    Task<AppResult<bool>> CheckOutAsync(string? coachId = null);
    Task<AppResult<IReadOnlyList<CoachPresenceRow>>> PresenceAsync();
}

public sealed class CoachService(ISupabaseGateway gw) : ICoachService
{
    readonly LinkCache photos = new(gw, "avatars", rememberMisses: true);

    public Task<AppResult<IReadOnlyList<CoachCard>>> ListCoachesAsync() => Safe.RunAsync<IReadOnlyList<CoachCard>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var coaches = await gw.ListAsync<Coach>();   // paused coaches stay listed, shown as closed
        var profiles = (await gw.ListAsync<Profile>()).ToDictionary(p => p.Id);
        var hires = await gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "active");
        var now = DateTime.UtcNow;
        var shifts = await gw.ListAsync<ShiftPost>(s => s.Status == "scheduled" && s.End > now);
        var inGym = (await gw.ListAsync<CoachVisit>(v => v.CheckedOutAt == null)).Select(v => v.CoachId).ToHashSet();
        // absent coaches are hidden, not grayed out; your own coach stays so Message and End hire keep working (D9)
        var shown = coaches.Where(c => inGym.Contains(c.UserId) || hires.Any(h => h.CoachId == c.UserId));
        return await Task.WhenAll(shown.Select(async c =>
        {
            var p = profiles.GetValueOrDefault(c.UserId);
            var name = p is null ? "Coach" : string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName;
            var photo = (await Safe.RunAsync(() => photos.UrlAsync(p?.AvatarPath))).Value;
            return new CoachCard(c, name, hires.Any(h => h.CoachId == c.UserId), hires.FirstOrDefault(h => h.CoachId == c.UserId)?.Id,
                shifts.Where(s => s.CoachId == c.UserId).OrderBy(s => s.Start).ToList(), photo, inGym.Contains(c.UserId));
        }));
    });

    public Task<AppResult<CoachVisit?>> MyVisitAsync() => Safe.RunAsync<CoachVisit?>(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<CoachVisit>(v => v.CoachId == uid && v.CheckedOutAt == null)).FirstOrDefault(); });

    public Task<AppResult<bool>> CheckInAsync(string? coachId = null) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("coach_check_in", coachId is null ? new() : new() { ["p_coach"] = coachId }); return true; });

    public Task<AppResult<bool>> CheckOutAsync(string? coachId = null) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("coach_check_out", coachId is null ? new() : new() { ["p_coach"] = coachId }); return true; });

    public Task<AppResult<IReadOnlyList<CoachPresenceRow>>> PresenceAsync() => Safe.RunAsync<IReadOnlyList<CoachPresenceRow>>(async () =>
    {
        var active = (await gw.ListAsync<Profile>(p => p.IsActive == true)).ToDictionary(p => p.Id, p => string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName);
        var open = (await gw.ListAsync<CoachVisit>(v => v.CheckedOutAt == null)).ToDictionary(v => v.CoachId, v => v.CheckedInAt);
        return (await gw.ListAsync<Coach>()).Where(c => active.ContainsKey(c.UserId))
            .Select(c => new CoachPresenceRow(c.UserId, active[c.UserId], open.TryGetValue(c.UserId, out var t) ? t : null))
            .OrderByDescending(r => r.InGym).ThenBy(r => r.Name).ToList();
    });

    public Task<AppResult<bool>> HireAsync(string coachId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync<int>("hire_coach", new() { ["p_coach_id"] = coachId }); return true; });

    public Task<AppResult<bool>> EndHireAsync(int hireId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("end_hire", new() { ["p_hire_id"] = hireId }); return true; });

    public Task<AppResult<Coach?>> MyCoachAsync() => Safe.RunAsync<Coach?>(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault(); });

    public async Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, decimal? rate, bool available) =>
        rate is { } r && !CoachRate.IsValid(r) ? AppResult<bool>.Fail(AppErrorKind.Validation, "bad_rate") : await Safe.RunAsync(async () =>
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
/// <summary>CanPropose: the coach may suggest another time. CanAccept: the member may accept the coach's live proposal (or ask again / decline).</summary>
public sealed record RequestRow(TimeRequest Request, string With, string Kind, bool CanRespond, bool CanCancel,
    string? Amenity = null, bool CanPropose = false, bool CanAccept = false, string? ProposalText = null)
{
    // declining always takes a reason; open slots are simply left alone
    public bool CanDecline => (CanRespond && Request.MemberId is not null) || CanAccept;
    public bool HasProposal => ProposalText is not null;
}
public sealed record AmenityChoice(int? Id, string Name);
public sealed record SessionRow(TrainingSession Session, string With, bool IsMine, bool CanManage, bool CanChange, int? ReservationId = null, string? Amenity = null)
{
    // the coach running a session on an amenity can add time until it ends (extend_reservation)
    public bool CanExtend => IsMine && ReservationId is not null && Session.Status == Status.Scheduled && Session.ScheduledEnd.ToUniversalTime() > DateTime.UtcNow;
}
public sealed record RequestMember(string MemberId, string Name);
public sealed record RequestsView(IReadOnlyList<RequestRow> Rows, bool IsCoach, IReadOnlyList<CoachCard> HiredCoaches, IReadOnlyList<RequestMember>? Trainees = null,
    IReadOnlyList<TrainingSession>? ActiveSessions = null, IReadOnlyList<Amenity>? Amenities = null);

public interface ISessionService
{
    Task<AppResult<RequestsView>> RequestsAsync();
    /// <summary>A member asks a hired coach for a session, optionally on an amenity (then it follows the booking grid and hours).</summary>
    Task<AppResult<bool>> RequestAsync(string coachId, DateTime startUtc, DateTime endUtc, string? message, int? amenityId = null);
    /// <summary>The coach suggests another time (and amenity) for a member's request, with a reason. Valid 24 h or until it starts.</summary>
    Task<AppResult<bool>> ProposeAsync(int requestId, DateTime startUtc, DateTime endUtc, int? amenityId, string reason);
    /// <summary>The member accepts the proposal they saw (by version): "confirmed", or "expired" when it ran out and went back to the coach.</summary>
    Task<AppResult<string>> AcceptProposalAsync(int requestId, int version);
    Task<AppResult<bool>> RequestAnotherTimeAsync(int requestId, DateTime startUtc, DateTime endUtc, int? amenityId, string? message);
    /// <summary>Declines with a reason: the coach a member's request, the member a proposal or an invitation.</summary>
    Task<AppResult<bool>> DeclineAsync(int requestId, string reason);
    Task<AppResult<bool>> PostAvailabilityAsync(DateTime startUtc, DateTime endUtc);
    Task<AppResult<bool>> InviteMemberAsync(string memberId, DateTime startUtc, DateTime endUtc);
    Task<AppResult<bool>> RespondAsync(int requestId, bool approve);
    Task<AppResult<bool>> CancelRequestAsync(int requestId);
    Task<AppResult<IReadOnlyList<SessionRow>>> SessionsAsync();
    /// <summary>The coach marks a session done (Completed) or missed (NoShow). Recorded as attendance, which also sets the status.</summary>
    Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status);
    Task<AppResult<bool>> RescheduleAsync(int sessionId, DateTime startUtc, DateTime endUtc);
    Task<AppResult<bool>> CancelSessionAsync(int sessionId);
    /// <summary>The coach adds minutes (30-minute steps) to a session on an amenity; the session and its reservation move together.</summary>
    Task<AppResult<bool>> ExtendAsync(int reservationId, int minutes);
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
        var now = DateTime.UtcNow;
        var requests = (await gw.ListAsync<TimeRequest>(r => r.Status == "pending")).Concat(await gw.ListAsync<TimeRequest>(r => r.Status == "proposed")).ToList();
        var amenities = await gw.ListAsync<Amenity>();
        string? AmenityName(int? id) => id is null ? null : amenities.FirstOrDefault(a => a.Id == id)?.Name ?? "Amenity";

        string CoachName(string id) => names.GetValueOrDefault(id, "Coach");
        string MemberName(string? id) => id is null ? "Open slot" : names.GetValueOrDefault(id, "Member");

        var rows = requests.OrderBy(r => r.RequestedStart).Select(r =>
        {
            var iAmCoachOfThis = r.CoachId == uid;
            var mine = (r.RequestedBy == "member" && r.MemberId == uid) || (r.RequestedBy == "coach" && iAmCoachOfThis);
            // a proposal that ran out is the coach's turn again (D8)
            var live = r.Status == "proposed" && r.ProposalExpiresAt?.ToUniversalTime() > now;
            var coachTurn = r.Status == "pending" || (r.Status == "proposed" && !live);
            var canRespond = !mine && (r.RequestedBy == "member" ? iAmCoachOfThis : !iAmCoachOfThis) && coachTurn;
            var kind = r.SessionId is not null ? (r.RequestedBy == "member" ? "Reschedule asked by member" : "Reschedule asked by coach")
                : live ? (iAmCoachOfThis ? "You proposed another time" : "Your coach proposed another time")
                : r.Status == "proposed" ? "Proposal expired"
                : r.RequestedBy == "member" ? "Requested by member" : r.MemberId is null ? "Open availability" : "Offered by coach";
            string? proposal = r.Status == "proposed" && r.ProposedStart is { } ps && r.ProposedEnd is { } pe
                ? $"{ps.ToLocalTime():ddd d MMM, h:mm tt}–{pe.ToLocalTime():h:mm tt}" + (AmenityName(r.ProposedAmenityId) is { } pa ? $" at {pa}" : "")
                  + $". {r.ProposalReason}" + (live ? $" Answer by {r.ProposalExpiresAt!.Value.ToLocalTime():ddd h:mm tt}." : " This proposal has expired.")
                : null;
            return new RequestRow(r, iAmCoachOfThis ? MemberName(r.MemberId) : CoachName(r.CoachId), kind, canRespond, mine, AmenityName(r.AmenityId),
                CanPropose: iAmCoachOfThis && r.RequestedBy == "member" && r.SessionId is null && coachTurn,
                CanAccept: r.MemberId == uid && r.RequestedBy == "member" && live, ProposalText: proposal);
        }).ToList();

        var hires = await gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "active");
        IReadOnlyList<CoachCard> hired = coaches.Where(c => hires.Any(h => h.CoachId == c.UserId))
            .Select(c => new CoachCard(c, CoachName(c.UserId), true, null)).ToList();
        IReadOnlyList<RequestMember> trainees = [];
        if (iAmCoach)
        {
            var mine = await gw.ListAsync<CoachHire>(h => h.CoachId == uid && h.Status == "active");
            var profiles = await gw.ListAsync<Profile>(p => p.IsActive == true);
            trainees = profiles.Where(p => mine.Any(h => h.MemberId == p.Id))
                .Select(p => new RequestMember(p.Id, MemberName(p.Id))).OrderBy(m => m.Name).ToList();
        }
        var active = await gw.ListAsync<TrainingSession>(s => s.Status == Status.Scheduled);
        // D7: a session only blocks new requests until its booked end has passed
        return new RequestsView(rows, iAmCoach, hired, trainees, active.Where(s => (s.CoachId == uid || s.MemberId == uid) && s.ScheduledEnd.ToUniversalTime() > now).ToList(),
            amenities.Where(a => a.IsActive).OrderBy(a => a.Name).ToList());
    });

    public async Task<AppResult<bool>> RequestAsync(string coachId, DateTime startUtc, DateTime endUtc, string? message, int? amenityId = null)
    {
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        var args = TimeArgs(coachId, startUtc, endUtc, message);
        if (amenityId is null) return await Safe.RunAsync(async () => { await gw.RpcAsync<int>("request_time", args); return true; });
        args["p_amenity_id"] = amenityId.Value;
        if (!args.ContainsKey("p_message")) args["p_message"] = null!;
        return await Safe.RunAsync(async () => { await gw.RpcAsync<int>("request_session", args); return true; });
    }

    public async Task<AppResult<bool>> ProposeAsync(int requestId, DateTime startUtc, DateTime endUtc, int? amenityId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_reason");
        return await Safe.RunAsync(async () =>
        {
            await gw.RpcAsync("propose_time", new() { ["p_request_id"] = requestId, ["p_start"] = startUtc.ToString("O"), ["p_end"] = endUtc.ToString("O"),
                ["p_amenity_id"] = amenityId!, ["p_reason"] = reason.Trim() });
            return true;
        });
    }

    public Task<AppResult<string>> AcceptProposalAsync(int requestId, int version) =>
        Safe.RunAsync(() => gw.RpcAsync<string>("accept_proposal", new() { ["p_request_id"] = requestId, ["p_version"] = version }));

    public Task<AppResult<bool>> RequestAnotherTimeAsync(int requestId, DateTime startUtc, DateTime endUtc, int? amenityId, string? message) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("request_another_time", new() { ["p_request_id"] = requestId, ["p_start"] = startUtc.ToString("O"), ["p_end"] = endUtc.ToString("O"),
            ["p_amenity_id"] = amenityId!, ["p_message"] = (string.IsNullOrWhiteSpace(message) ? null : message.Trim())! });
        return true;
    });

    public Task<AppResult<bool>> ExtendAsync(int reservationId, int minutes) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("extend_reservation", new() { ["p_id"] = reservationId, ["p_minutes"] = minutes }); return true; });

    public async Task<AppResult<bool>> DeclineAsync(int requestId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_reason");
        return await Safe.RunAsync(async () => { await gw.RpcAsync("decline_time_request", new() { ["p_request_id"] = requestId, ["p_reason"] = reason.Trim() }); return true; });
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

    public async Task<AppResult<bool>> InviteMemberAsync(string memberId, DateTime startUtc, DateTime endUtc)
    {
        if (string.IsNullOrWhiteSpace(memberId)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        if (endUtc <= startUtc) return AppResult<bool>.Fail(AppErrorKind.Validation, "end must be after start");
        return await Safe.RunAsync(async () =>
        {
            await gw.RpcAsync<int>("request_member_time", new()
            {
                ["p_member_id"] = memberId, ["p_start"] = startUtc.ToString("O"), ["p_end"] = endUtc.ToString("O")
            });
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
        var held = (await gw.ListAsync<Reservation>(r => r.Status == "confirmed")).Where(r => r.TrainingSessionId is not null).ToDictionary(r => r.TrainingSessionId!.Value);
        var amenities = held.Count == 0 ? [] : await gw.ListAsync<Amenity>();
        return sessions.OrderBy(s => s.ScheduledStart).Select(s =>
        {
            var isMine = s.CoachId == uid;
            var with = isMine ? s.MemberId : s.CoachId;
            var r = held.GetValueOrDefault(s.Id);
            return new SessionRow(s, names.GetValueOrDefault(with, "Unknown"), isMine, isMine && s.Status == Status.Scheduled, s.Status == Status.Scheduled,
                r?.Id, r is null ? null : amenities.FirstOrDefault(a => a.Id == r.AmenityId)?.Name ?? "Amenity");
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
    /// <summary>The signed-in member's permanent QR code (0034), or null for anyone who is not a member.</summary>
    Task<AppResult<string?>> MyCodeAsync();
    /// <summary>Front desk and admins: who a scanned code belongs to and their live status. Every scan is logged by the server.</summary>
    Task<AppResult<MemberScan>> ScanAsync(string code);
    /// <summary>Gym check-ins on one Manila day, earliest first.</summary>
    Task<AppResult<IReadOnlyList<CheckInRow>>> CheckInsOnAsync(DateOnly manilaDay);
}

/// <summary>What scan_member says about a code: result is active, pending, expired, inactive or not_found.</summary>
public sealed class MemberScan
{
    public string Result { get; set; } = "";
    public string? Member { get; set; }
    public string? Name { get; set; }
    public DateTime? Expires { get; set; }

    public bool CanCheckIn => Result == "active" && Member is not null;
    public string StatusText => Result switch
    {
        "active" => "Active", "pending" => "Pending: a membership payment is waiting to be verified", "expired" => "Expired: no active membership",
        "inactive" => "Inactive: this account is archived", _ => "Not found: this code does not belong to any member"
    };
    public string ExpiryText => Expires is { } e ? (Result == "active" ? "Membership ends " : "Membership ended ") + RevenuePdf.Manila(e) + " (Manila)" : "No end date";
}

public sealed record CheckInRow(string Name, DateTime At)
{
    public string TimeText => At.AddHours(8).ToString("HH:mm", CultureInfo.InvariantCulture);   // Manila has no daylight saving
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

    public Task<AppResult<string?>> MyCodeAsync() => Safe.RunAsync<string?>(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<MemberCode>(c => c.UserId == uid)).FirstOrDefault()?.Code; });

    public Task<AppResult<MemberScan>> ScanAsync(string code) =>
        Safe.RunAsync(() => gw.RpcAsync<MemberScan>("scan_member", new() { ["p_code"] = code.Trim() }));

    public Task<AppResult<IReadOnlyList<CheckInRow>>> CheckInsOnAsync(DateOnly manilaDay) => Safe.RunAsync<IReadOnlyList<CheckInRow>>(async () =>
    {
        var from = DateTime.SpecifyKind(manilaDay.ToDateTime(TimeOnly.MinValue).AddHours(-8), DateTimeKind.Utc);   // Manila has no daylight saving
        var to = from.AddDays(1);
        var rows = await gw.ListAsync<GymCheckIn>(c => c.CheckedInAt >= from && c.CheckedInAt < to);
        var names = await Directory_.UserNamesAsync(gw);
        return rows.OrderBy(c => c.CheckedInAt)
            .Select(c => new CheckInRow(c.MemberId is { } m ? names.GetValueOrDefault(m, "Member") : "Walk-in", c.CheckedInAt)).ToList();
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
    public string RoleText => string.Join(", ", Roles.Select(RoleName));
    /// <summary>What a role is called on screen (D15: the admin is the super admin).</summary>
    public static string RoleName(string role) => role switch { "admin" => "super admin", "subadmin" => "sub-admin", _ => role };
    public bool IsCoach => Roles.Contains("coach");
    public bool IsEmployee => Roles.Contains("employee");
    public string CoachActionText => IsCoach ? "Remove coach" : "Make coach";
    public string EmployeeActionText => IsEmployee ? "Remove employee" : "Make employee";
    public bool IsSubAdmin => Roles.Contains("subadmin");
    /// <summary>Fixed from the app (0037): no role changes, no deactivating, no deleting.</summary>
    public bool IsSuperAdmin => Roles.Contains("admin");
    public string SubAdminActionText => IsSubAdmin ? "Remove sub-admin" : "Make sub-admin";
    /// <summary>Front desk, sub-admin or super admin: only the super admin manages these accounts (D15).</summary>
    public bool IsStaff => Roles.Any(r => r is "admin" or "subadmin" or "employee");
    public string ActiveActionText => IsActive ? "Deactivate" : "Reactivate";
    public string ActiveStatus => IsActive ? "active" : "inactive";
}
public sealed record AuditRow(AuditEntry Entry, string Actor);
public sealed record DiscountRow(Discount Discount, string State, long Used = 0, long Pending = 0)
{
    // used = verified + voided; pending payments hold a place too
    public string Usage => (Discount.MaxUses is { } max ? $"{Used} of {max} used" : $"{Used} used, no limit") + (Pending > 0 ? $", {Pending} pending" : "");
}
public sealed record DiscountUse(long Id, long Used, long Pending);

public interface IAdminService
{
    /// <summary>Everyone not deleted, active or deactivated.</summary>
    Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync();
    /// <summary>True for the super admin (the admin role); false for a sub-admin and everyone else (D15).</summary>
    Task<AppResult<bool>> IsSuperAdminAsync();
    Task<AppResult<bool>> AssignRoleAsync(string userId, string role);
    Task<AppResult<bool>> RevokeRoleAsync(string userId, string role);
    Task<AppResult<bool>> SetActiveAsync(string userId, bool active);
    Task<AppResult<bool>> CreateUserAsync(string firstName, string lastName, string email, string role, string password);
    Task<AppResult<bool>> ResetPasswordAsync(string userId, string password);
    Task<AppResult<bool>> DeletePackageAsync(MembershipPackage package);
    Task<AppResult<IReadOnlyList<DiscountRow>>> DiscountsAsync();
    Task<AppResult<bool>> SaveDiscountAsync(Discount discount);
    Task<AppResult<bool>> DeleteDiscountAsync(Discount discount);
    Task<AppResult<bool>> SavePackageAsync(MembershipPackage package);
    Task<AppResult<bool>> SaveAmenityAsync(Amenity amenity);
    Task<AppResult<string>> SetAmenityPhotoAsync(int amenityId, string fileName, byte[] data);
    Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync();
    Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync();
    Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to);
    /// <summary>Every receipt and approved refund in the range, all pages, for the PDF export.</summary>
    Task<AppResult<IReadOnlyList<RevenueLine>>> RevenueLinesAsync(DateOnly from, DateOnly to);
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
        return profiles.Where(p => p.DeletedAt is null).Select(p => new UserRow(p.Id, string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName, p.IsActive,
            links.Where(l => l.UserId == p.Id).Select(l => roles.First(r => r.RoleId == l.RoleId).Name).OrderBy(n => n).ToList())).ToList();
    });

    public Task<AppResult<bool>> IsSuperAdminAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var mine = (await gw.ListAsync<UserRole>(u => u.UserId == uid)).Select(u => u.RoleId).ToHashSet();
        return (await gw.ListAsync<Role>()).Any(r => r.Name == "admin" && mine.Contains(r.RoleId));
    });

    public Task<AppResult<bool>> AssignRoleAsync(string userId, string role) => Rpc("assign_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> RevokeRoleAsync(string userId, string role) => Rpc("revoke_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> SetActiveAsync(string userId, bool active) => Rpc("set_user_active", new() { ["p_user"] = userId, ["p_active"] = active });

    // accounts are made by an Edge Function: it holds the service-role key, which the app never has
    public async Task<AppResult<bool>> CreateUserAsync(string firstName, string lastName, string email, string role, string password)
    {
        firstName = firstName.Trim(); lastName = lastName.Trim();
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || !email.Contains('@') || password.Length < 6 || role is not ("member" or "coach" or "employee" or "subadmin"))
            return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        if (!AccountRules.IsName(firstName, lastName)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name");   // the database refuses it too (0012, 0035)
        return await Safe.RunAsync(async () =>
        {
            // name goes along for a create-user function deployed before first/last name existed
            await gw.InvokeFunctionAsync("create-user", new()
            {
                ["first_name"] = firstName, ["last_name"] = lastName, ["name"] = NameParts.Join(firstName, lastName),
                ["email"] = email.Trim(), ["role"] = role, ["password"] = password
            });
            return true;
        });
    }

    public async Task<AppResult<bool>> ResetPasswordAsync(string userId, string password)
    {
        if (password.Length < 6) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () => { await gw.InvokeFunctionAsync("reset-password", new() { ["user_id"] = userId, ["password"] = password }); return true; });
    }

    public Task<AppResult<bool>> DeletePackageAsync(MembershipPackage p) => Rpc("archive_catalog_record", new() { ["p_type"] = "plan", ["p_id"] = p.Id, ["p_reason"] = "Deactivated gym plan" });

    public Task<AppResult<IReadOnlyList<DiscountRow>>> DiscountsAsync() => Safe.RunAsync<IReadOnlyList<DiscountRow>>(async () =>
    {
        var now = DateTime.UtcNow;
        var uses = (await gw.RpcAsync<List<DiscountUse>>("discount_usage", new()) ?? []).ToDictionary(u => u.Id);
        return (await gw.ListAsync<Discount>()).OrderByDescending(d => d.ExpiresAt)
            .Select(d => new DiscountRow(d, !d.IsActive ? "inactive" : d.ExpiresAt <= now ? "expired" : "active",
                uses.GetValueOrDefault(d.Id)?.Used ?? 0, uses.GetValueOrDefault(d.Id)?.Pending ?? 0)).ToList();
    });

    public async Task<AppResult<bool>> SaveDiscountAsync(Discount d)
    {
        if (string.IsNullOrWhiteSpace(d.Code) || d.Percent is < 1 or > 100) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_discount");
        if (d.MaxUses < 1 || d.Id == 0 && d.MaxUses is null) return AppResult<bool>.Fail(AppErrorKind.Validation, "limit_required");
        d.Code = d.Code.Trim().ToUpperInvariant();
        return await Safe.RunAsync(async () => { if (d.Id == 0) await gw.InsertAsync(d); else await gw.UpdateAsync(d); return true; });
    }

    public Task<AppResult<bool>> DeleteDiscountAsync(Discount d) => Rpc("archive_catalog_record", new() { ["p_type"] = "code", ["p_id"] = d.Id, ["p_reason"] = "Deactivated code" });

    public Task<AppResult<bool>> SavePackageAsync(MembershipPackage p) =>
        Safe.RunAsync(async () => { if (p.Id == 0) await gw.InsertAsync(p); else await gw.UpdateAsync(p); return true; });
    public Task<AppResult<bool>> SaveAmenityAsync(Amenity a) =>
        Safe.RunAsync(async () => { if (a.Id == 0) await gw.InsertAsync(a); else await gw.UpdateAsync(a); return true; });

    public Task<AppResult<string>> SetAmenityPhotoAsync(int amenityId, string fileName, byte[] data)
    {
        if (!ProfileService.IsAcceptableImage(fileName, data)) return Task.FromResult(AppResult<string>.Fail(AppErrorKind.Validation, "bad_image"));
        return Safe.RunAsync(async () =>
        {
            var amenity = (await gw.ListAsync<Amenity>(a => a.Id == amenityId)).FirstOrDefault() ?? throw new Exception("not_found");
            if (!amenity.IsActive) throw new Exception("record_archived");
            var path = await gw.UploadAsync("gym-assets", $"amenities/{amenityId}/{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}", data);
            // Only the image changes: a catalog row on another screen may already have newer details or status.
            await gw.UpdateWhereAsync<Amenity, string?>(a => a.Id == amenityId, a => a.ImagePath, path);
            return path;
        });
    }

    public Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync() => Safe.RunAsync(() => gw.ListAsync<MembershipPackage>());
    public Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync() => Safe.RunAsync(() => gw.ListAsync<Amenity>());

    public async Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to)
    {
        if (to < from) return AppResult<IReadOnlyList<RevenueRow>>.Fail(AppErrorKind.Validation, "to before from");
        return await Safe.RunAsync(async () => (IReadOnlyList<RevenueRow>)(await gw.RpcAsync<List<RevenueRow>>("revenue_summary",
            new() { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd") }) ?? []));
    }

    public async Task<AppResult<IReadOnlyList<RevenueLine>>> RevenueLinesAsync(DateOnly from, DateOnly to)
    {
        if (to < from) return AppResult<IReadOnlyList<RevenueLine>>.Fail(AppErrorKind.Validation, "to before from");
        return await Safe.RunAsync(async () =>
        {
            const int page = 500;   // the server caps rows per request, so read until a short page
            var all = new List<RevenueLine>();
            while (true)
            {
                var rows = await gw.RpcAsync<List<RevenueLine>>("revenue_lines", new()
                    { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd"), ["p_offset"] = all.Count, ["p_limit"] = page }) ?? [];
                all.AddRange(rows);
                if (rows.Count < page) return (IReadOnlyList<RevenueLine>)all;
            }
        });
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
        static bool OnGrid(TimeSpan t) => t.Seconds == 0 && t.Minutes is 0 or 30 && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
        if (!OnGrid(s.OpensAt) || !OnGrid(s.ClosesAt) || s.ClosesAt <= s.OpensAt) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_hours");
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
