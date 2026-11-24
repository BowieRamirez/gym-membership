using System.Globalization;
using System.Text.RegularExpressions;
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>G16 (0065): closed days from today (Manila) on, with their reasons; every calendar that offers a day reads this.</summary>
public static class GymClosures
{
    public static async Task<IReadOnlyDictionary<DateOnly, string>> ByDayAsync(ISupabaseGateway gw)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)).ToDateTime(TimeOnly.MinValue);
        return (await gw.ListAsync<GymClosure>(c => c.ClosedOn >= today)).ToDictionary(c => c.Day, c => c.Reason);
    }
}

/// <summary>G18: what closing a day would cancel, as closure_preview counts it.</summary>
public sealed record ClosurePreview(int Bookings, int Paid, int Sessions, int Shifts, int Requests)
{
    static string N(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
    public string Text => $"{N(Bookings, "booking")}{(Paid > 0 ? $" ({Paid} paid, refunded)" : "")}, {N(Sessions, "session")} and {N(Shifts, "shift")} will be cancelled."
        + (Requests > 0 ? $" {N(Requests, "session request")} will be declined." : "");
}

static class Directory_
{
    /// <summary>Every profile the caller may read, by id. The full name, or the username if none was given.</summary>
    public static async Task<Dictionary<string, string>> UserNamesAsync(ISupabaseGateway gw) =>
        (await gw.ListAsync<PublicProfile>()).ToDictionary(p => p.Id, p => p.FullName);   // 0140: names only; emails stay with staff
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
    Task<AppResult<bool>> RegisterAsync(string firstName, string lastName, string email, string password, bool agreedToTerms);
    /// <summary>The register page's live check. An address that isn't a valid email isn't looked up.</summary>
    Task<AppResult<bool>> EmailInUseAsync(string email);
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
/// <summary>Cashier plan Y2: the temporary password the desk hands a member it registers; they change it at first sign-in.
/// 10 characters from letters and digits a person can read aloud (no 0/O, 1/l/I), always with a letter and a digit.</summary>
public static class TempPassword
{
    const string Letters = "abcdefghjkmnpqrstuvwxyz", Digits = "23456789";
    public static string New()
    {
        string Pick(string from, int n) => new(Enumerable.Range(0, n).Select(_ => from[System.Security.Cryptography.RandomNumberGenerator.GetInt32(from.Length)]).ToArray());
        return Pick(Letters, 6) + Pick(Digits, 4);
    }
}

public static partial class AccountRules
{
    public const int MinName = 2, MaxName = 60, MaxEmailLocal = 64, MaxEmail = 100, MinPassword = 8, MaxPassword = 64;

    // letters (any script), joined by single spaces, hyphens or apostrophes: "Mary-Ann O'Brien", "Juan dela Cruz". No separate accent
    // marks: the database's [[:alpha:]] refuses them (checked live 2026-11-08), so a name is composed first ("e" + accent becomes "é").
    [GeneratedRegex(@"^\p{L}+(?:[ '\-]\p{L}+)*$")] private static partial Regex NameShape();
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")] private static partial Regex EmailShape();

    public static bool IsName(string name) => (name = name.Normalize()).Length is >= MinName and <= MaxName && NameShape().IsMatch(name);

    /// <summary>Both parts are names, and "first last" fits the 60 characters the database allows for the full name.</summary>
    public static bool IsName(string first, string last) => IsName(first) && IsName(last) && NameParts.Join(first, last).Length <= MaxName;

    /// <summary>A code from the email: exactly six digits.</summary>
    public static bool IsCode(string code) => code.Length == 6 && code.All(char.IsAsciiDigit);

    public static bool IsEmail(string email) =>
        email.Length <= MaxEmail && EmailShape().IsMatch(email) && email.IndexOf('@') <= MaxEmailLocal;

    /// <summary>
    /// D5, for new passwords only: 8 to 64 characters with a letter and a number. Letters and numbers are a to z and 0 to 9, as Supabase
    /// Auth's "letters_digits" counts them, and as supabase/functions/_shared/rules.ts checks for staff-made passwords.
    /// </summary>
    public static bool IsPassword(string password) => PasswordChecks(password) is (true, true, _);

    /// <summary>The checklist under a new password. The first two are required; mixed case makes it stronger.</summary>
    public static (bool Length, bool LetterAndNumber, bool Mixed) PasswordChecks(string password) =>
        (password.Length is >= MinPassword and <= MaxPassword, password.Any(char.IsAsciiLetter) && password.Any(char.IsAsciiDigit),
         password.Any(char.IsLower) && password.Any(char.IsUpper));

    /// <summary>0 when empty, then 1 weak to 4 strong: one point per check passed, and 4 for all three at 12 or more characters.</summary>
    public static int PasswordStrength(string password)
    {
        if (password.Length == 0) return 0;
        var (length, letterAndNumber, mixed) = PasswordChecks(password);
        var passed = (length ? 1 : 0) + (letterAndNumber ? 1 : 0) + (mixed ? 1 : 0);
        return passed == 3 && password.Length >= 12 ? 4 : Math.Max(passed, 1);
    }

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

    public async Task<AppResult<bool>> RegisterAsync(string firstName, string lastName, string email, string password, bool agreedToTerms)
    {
        if (!agreedToTerms) return AppResult<bool>.Fail(AppErrorKind.Validation, "terms_required");   // G25 (0069)
        firstName = firstName.Trim().Normalize(); lastName = lastName.Trim().Normalize(); email = email.Trim();
        if (!AccountRules.IsName(firstName, lastName)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name");
        if (!AccountRules.IsEmail(email)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_email");
        if (!AccountRules.IsPassword(password)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_password");
        return await Safe.RunAsync(async () => { await gw.SignUpAsync(email, password, firstName, lastName, TermsInfo.Version); return true; });
    }

    public Task<AppResult<bool>> EmailInUseAsync(string email)
    {
        email = email.Trim();
        return AccountRules.IsEmail(email) ? Safe.RunAsync(() => gw.EmailInUseAsync(email)) : Task.FromResult(AppResult<bool>.Success(false));
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
        if (!AccountRules.IsPassword(next)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_password");
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
        var profileTask = gw.ListAsync<Profile>(p => p.Id == uid);
        var mineTask = gw.ListAsync<UserRole>(u => u.UserId == uid);
        var rolesTask = gw.ListAsync<Role>();
        await Task.WhenAll(profileTask, mineTask, rolesTask);
        // Auth itself still lets a deactivated person sign in, so the app turns them away here
        if (profileTask.Result.FirstOrDefault() is { IsActive: false })
        {
            await gw.SignOutAsync();
            throw new Exception("forbidden");
        }
        var roles = rolesTask.Result;
        return RoleResolver.Pick(mineTask.Result.Select(m => roles.FirstOrDefault(r => r.RoleId == m.RoleId)?.Name).OfType<string>());
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

    public static async Task<PaymentLookup> LoadAsync(ISupabaseGateway gw)
    {
        var umpsTask = gw.ListAsync<UserMembershipPackage>();
        var renTask = gw.ListAsync<MembershipRenewal>();
        var uaTask = gw.ListAsync<UserAmenity>();
        var pkgTask = gw.ListAsync<MembershipPackage>();
        var amTask = gw.ListAsync<Amenity>();
        await Task.WhenAll(umpsTask, renTask, uaTask, pkgTask, amTask);
        return new(umpsTask.Result, renTask.Result, uaTask.Result, pkgTask.Result, amTask.Result);
    }

    string PackageName(int umpId) => _packages.FirstOrDefault(k => k.Id == _umps.FirstOrDefault(u => u.Id == umpId)?.MembershipPackageId)?.Name ?? "Membership";

    public string Item(Payment p) => p.Kind switch
    {
        PayKind.Membership => p.PackageSnapshotName ?? (p.UserMembershipPackageId is { } u ? PackageName(u) : "Membership"),
        PayKind.Renewal => "Extension: " + (p.PackageSnapshotName ?? (_renewals.FirstOrDefault(r => r.Id == p.RenewalId) is { } r ? PackageName(r.UserMembershipPackageId) : "Membership")),
        PayKind.Amenity => _catalog.FirstOrDefault(a => a.Id == _amenities.FirstOrDefault(x => x.Id == p.UserAmenityId)?.AmenityId)?.Name ?? "Amenity",
        PayKind.CoachAddon => p.PackageSnapshotName is { Length: > 0 } name ? $"Coach add-on ({name})" : "Coach add-on",
        PayKind.WalkIn => "Day pass",   // cashier plan Y3
        _ => PayKind.Name(p.Kind)
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

/// <summary>Owner, 2026-11-18: the live server's times arrive as local time, the demo's as UTC. Always go through here before comparing with UtcNow or adding Manila's 8 hours.</summary>
public static class ManilaTime
{
    public static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : t;
    public static DateTime? Utc(DateTime? t) => t is { } v ? Utc(v) : null;
    public static DateTime Of(DateTime t) => Utc(t).AddHours(8);   // Manila has no daylight saving
    public static DateTime? Of(DateTime? t) => t is { } v ? Of(v) : null;
}

/// <summary>One of the member's payments. Online (qr) payments are paid through Xendit's checkout (Xendit plan, 2026-11-16).</summary>
public sealed record PaymentItem(int Id, string What, string PaymentCode, string Kind, string Method, decimal Amount, decimal DiscountAmount, string Status,
    DateTime CreatedAt, DateTime? ExpiresAt, string? ReferenceNo, bool HasProof, string? ReceiptNo, string? RejectedReason, int? ItemId, int? PackageSnapshotId = null,
    bool HasCheckout = false, bool Refunded = false)
{
    /// <summary>An online payment the member still has to pay (a proof sent before the switch is with the desk instead).</summary>
    bool NotExpired => ExpiresAt is not { } e || ManilaTime.Utc(e) > DateTime.UtcNow;
    public bool NeedsPayment => Status == Models.Status.Pending && Method == PayMethod.Qr && !HasProof && NotExpired;
    public bool AwaitingReview => Status == Models.Status.Pending && HasProof;
    /// <summary>Cash plan 2026-11-17: the member chose cash; it waits on the desk's Payments to verify.</summary>
    public bool PaysAtDesk => Status == Models.Status.Pending && Method == PayMethod.Cash && NotExpired;
    /// <summary>A membership or extension payment nobody has verified yet: the member can still withdraw it.</summary>
    public bool CanCancel => Status == Models.Status.Pending && Kind is PayKind.Membership or PayKind.Renewal or PayKind.CoachAddon;   // 0140: coach access too
    /// <summary>Owner fixes 2026-11-19 Q8: how it was paid and its status, in the member's words.</summary>
    public string MethodText => Method switch { PayMethod.Qr => "Online", PayMethod.Cash => "Cash", PayMethod.Gcash => "GCash", _ => Method };
    public string StatusText => PaymentStatus.Text(Status, Refunded);
    /// <summary>Rejected and expired payments are final; the member pays again with a fresh one.</summary>
    public bool CanPayAgain => Status is Models.Status.Rejected or Models.Status.Expired && ItemId is not null && Kind != PayKind.Amenity;   // F12: an amenity is booked again on Book an amenity
    public string Detail => Status switch
    {
        Models.Status.Rejected => RejectedReason ?? "Rejected",
        Models.Status.Expired => "Not paid in time",
        Models.Status.Verified => ReceiptNo ?? "",
        _ when AwaitingReview => "Waiting for the front desk to check it",
        _ when PaysAtDesk => "Pay at the front desk",
        _ => ""
    };
}

public sealed record CoachAccessOffer(
    [property: System.Text.Json.Serialization.JsonPropertyName("offered")] bool Offered,
    [property: System.Text.Json.Serialization.JsonPropertyName("term_id")] int? TermId,
    [property: System.Text.Json.Serialization.JsonPropertyName("package_id")] int? PackageId,
    [property: System.Text.Json.Serialization.JsonPropertyName("package_name")] string? PackageName,
    [property: System.Text.Json.Serialization.JsonPropertyName("full_price")] decimal? FullPrice,
    [property: System.Text.Json.Serialization.JsonPropertyName("price")] decimal? Price,
    [property: System.Text.Json.Serialization.JsonPropertyName("days_left")] int? DaysLeft,
    [property: System.Text.Json.Serialization.JsonPropertyName("duration_days")] int? DurationDays,
    [property: System.Text.Json.Serialization.JsonPropertyName("ends_at")] DateTime? EndsAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("is_day_pass")] bool IsDayPass,
    [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string? Reason);

public interface IMembershipService
{
    Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync();
    /// <summary>G4 (0060): codes staff chose to show that this member can still use, each with FitsText filled from the catalog.</summary>
    Task<AppResult<IReadOnlyList<AvailableCode>>> AvailableCodesAsync();
    /// <summary>Owner fixes 2026-11-19 Q12 (0107): checks a discount code as it's typed, without writing to payments.</summary>
    Task<AppResult<CodeCheck>> CheckCodeAsync(string code, string kind, int itemId);
    Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync();
    Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync();
    Task<AppResult<Access>> AccessAsync();
    /// <summary>Starts a membership: a pending QR payment with a code to pay. The membership turns on when staff verify it.</summary>
    Task<AppResult<Payment>> AvailAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr);
    Task<AppResult<Payment>> RenewAsync(int membershipId, string? discountCode = null, string method = PayMethod.Qr);
    /// <summary>Xendit's checkout for an online payment: a link to open, or Paid when it is already paid (demo mode pays at once).</summary>
    Task<AppResult<OnlineCheckout>> PayOnlineAsync(int paymentId);
    /// <summary>Asks the server whether the checkout was paid, and confirms it if Xendit's notice hasn't arrived (X10).</summary>
    Task<AppResult<bool>> CheckOnlineAsync(int paymentId);
    /// <summary>Starts a fresh payment for the same item after a rejection or expiry.</summary>
    Task<AppResult<Payment>> PayAgainAsync(PaymentItem item, string? code = null);
    /// <summary>Phase 16 (E13): reads whether the member's active term can buy coach access and its scaled price.</summary>
    Task<AppResult<CoachAccessOffer?>> CoachAccessOfferAsync();
    /// <summary>Phase 16 (E13): creates a coach_addon online payment for the member's active term.</summary>
    Task<AppResult<Payment>> BuyCoachAccessAsync(string? discountCode = null);
    Task<AppResult<GymSettings>> GymAsync();
    /// <summary>G16: closed days from today on, with their reasons.</summary>
    Task<AppResult<IReadOnlyDictionary<DateOnly, string>>> ClosedDaysAsync();
}

/// <summary>xendit-checkout's answer: the checkout link to open, or Paid.</summary>
public sealed record OnlineCheckout(string? Url, bool Paid);

public enum OnlineWait { StillWaiting, Paid, Over }

/// <summary>Owner, 2026-11-18: the desk's screens ask by themselves while a customer pays online. Quiet: a failed look just means "ask again".</summary>
public static class DeskOnlineWait
{
    public static async Task<OnlineWait> PollAsync(IPaymentStaffService svc, int paymentId)
    {
        if (await svc.CheckOnlineAsync(paymentId) is { Ok: true, Value: true }) return OnlineWait.Paid;
        if (await svc.PaymentAsync(paymentId) is not { Ok: true, Value: { } p }) return OnlineWait.StillWaiting;
        if (p.Status == Status.Verified) return OnlineWait.Paid;
        return p.Status != Status.Pending || p.ExpiresAt?.ToUniversalTime() <= DateTime.UtcNow ? OnlineWait.Over : OnlineWait.StillWaiting;
    }
}

/// <summary>xendit-checkout, for the member's own payment or one the desk recorded (cashier plan Y6).</summary>
public static class OnlinePay
{
    public static Task<AppResult<OnlineCheckout>> OpenAsync(ISupabaseGateway gw, int paymentId) => Safe.RunAsync(async () =>
    {
        if ((await gw.ListAsync<Payment>(p => p.Id == paymentId)).FirstOrDefault()?.Status == Status.Verified)
            return new OnlineCheckout(null, true);
        var answer = System.Text.Json.JsonDocument.Parse(await gw.InvokeFunctionAsync("xendit-checkout", new() { ["payment_id"] = paymentId })).RootElement;
        return new OnlineCheckout(answer.TryGetProperty("url", out var url) ? url.GetString() : null,
            answer.TryGetProperty("paid", out var paid) && paid.GetBoolean());
    });

    public static Task<AppResult<bool>> CheckAsync(ISupabaseGateway gw, int paymentId) => Safe.RunAsync(async () =>
        System.Text.Json.JsonDocument.Parse(await gw.InvokeFunctionAsync("xendit-checkout", new() { ["payment_id"] = paymentId, ["check"] = true }))
            .RootElement.TryGetProperty("paid", out var paid) && paid.GetBoolean());
}

/// <summary>What a member may open: full pages need an active plan; a pending payment only changes the wording.</summary>
public sealed record Access(bool Active, bool PaymentPending);

public sealed class MembershipService(ISupabaseGateway gw) : IMembershipService
{
    public Task<AppResult<Access>> AccessAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var activeTask = gw.RpcAsync<bool>("is_active_member");
        var paysTask = gw.ListAsync<Payment>(p => p.MemberId == uid);
        await Task.WhenAll(activeTask, paysTask);
        var active = activeTask.Result;
        // only what the member menu shows: a plan payment waiting matters to the locked pages; an amenity down payment must not rebuild the menu
        return new Access(active, !active && paysTask.Result.Any(p => p.Status == Status.Pending && p.Kind is PayKind.Membership or PayKind.Renewal));
    });

    public Task<AppResult<IReadOnlyList<MembershipPackage>>> ListPackagesAsync() =>
        Safe.RunAsync(() => gw.ListAsync<MembershipPackage>(p => p.IsActive == true));

    public Task<AppResult<IReadOnlyList<AvailableCode>>> AvailableCodesAsync() => Safe.RunAsync<IReadOnlyList<AvailableCode>>(async () =>
    {
        var codesTask = gw.RpcAsync<List<AvailableCode>>("my_discount_codes", new());
        var pkgsTask = gw.ListAsync<MembershipPackage>(p => p.IsActive == true);
        var amsTask = gw.ListAsync<Amenity>(a => a.IsActive == true);
        await Task.WhenAll(codesTask, pkgsTask, amsTask);
        var codes = codesTask.Result ?? [];
        if (codes.Count == 0) return codes;
        var packages = pkgsTask.Result.ToDictionary(p => p.Id, p => p.Name);
        var amenities = amsTask.Result.ToDictionary(a => a.Id, a => a.Name);
        foreach (var c in codes) c.FitsText = DiscountFits.Text(c.AllPackages, c.PackageIds, c.AllAmenities, c.AmenityIds, packages, amenities);
        return codes;
    });

    public Task<AppResult<CodeCheck>> CheckCodeAsync(string code, string kind, int itemId) => Safe.RunAsync(async () =>
    {
        var rows = await gw.RpcAsync<List<CodeCheck>>("check_discount_code",
            new() { ["p_code"] = code.Trim(), ["p_kind"] = kind, ["p_item"] = itemId });
        return rows?.FirstOrDefault() ?? new CodeCheck { Reason = "invalid_code" };
    });

    public static MembershipRow ToRow(UserMembershipPackage m, MembershipPackage? p, DateTime now)
    {
        int? left = m.EndsAt is { } e ? Math.Max(0, (int)Math.Ceiling((e - now).TotalDays)) : null;
        var canRenew = (m.Status == Status.Active && left <= 7) || m.Status == Status.Expired;
        return new(m.Id, p?.Name ?? "Membership", m.Status, m.EndsAt, left, p?.DurationDays ?? 30, canRenew, p?.Price ?? 0);
    }

    public Task<AppResult<IReadOnlyList<MembershipRow>>> MyMembershipsAsync() => Safe.RunAsync<IReadOnlyList<MembershipRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        return MembershipRows(await gw.ListAsync<MembershipTerm>(t => t.MemberId == uid), DateTime.UtcNow);
    });

    /// <summary>The terms that have started, newest end first. A term bought just now counts at once (it was hidden for 8 hours on the live server).</summary>
    public static IReadOnlyList<MembershipRow> MembershipRows(IEnumerable<MembershipTerm> terms, DateTime nowUtc) =>
        terms.Select(SubscriptionService.InUtc).Where(t => t.CancelledAt is null && t.StartsAt <= nowUtc).OrderByDescending(t => t.EndsAt)
            .Select(t => new MembershipRow(t.MembershipId,t.PackageName,t.EndsAt > nowUtc ? Status.Active : Status.Expired,
                t.EndsAt,Math.Max(0,(int)Math.Ceiling((t.EndsAt-nowUtc).TotalDays)),t.DurationDays,false,t.AmountPaid)).ToList();

    public Task<AppResult<IReadOnlyList<PaymentItem>>> MyPaymentsAsync() => Safe.RunAsync<IReadOnlyList<PaymentItem>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var paysTask = gw.ListAsync<Payment>(p => p.MemberId == uid);
        var refTask = gw.ListAsync<RefundRequest>(r => r.MemberId == uid && r.Status == "paid");
        var lookTask = PaymentLookup.LoadAsync(gw);
        await Task.WhenAll(paysTask, refTask, lookTask);
        var pays = paysTask.Result;
        var refunded = refTask.Result.Select(r => r.PaymentId).ToHashSet();
        var look = lookTask.Result;
        return pays.OrderByDescending(p => p.CreatedAt).Select(p => new PaymentItem(p.Id,
            p.DiscountAmount > 0 ? look.Item(p) + " (discount)" : look.Item(p), p.PaymentCode, p.Kind, p.Method, p.Amount, p.DiscountAmount, p.Status,
            p.CreatedAt, p.ExpiresAt, p.ReferenceNo, p.ProofPath is not null, p.ReceiptNo, p.RejectedReason, look.ItemId(p), p.PackageSnapshotId, p.XenditId is not null,
            refunded.Contains(p.Id))).ToList();
    });

    static Dictionary<string, object> WithCode(Dictionary<string, object> args, string? code)
    { if (!string.IsNullOrWhiteSpace(code)) args["p_discount_code"] = code.Trim(); return args; }

    // 0084: cash at the desk is sent; online (qr) is the server's default, so older servers see no new argument
    static Dictionary<string, object> WithMethod(Dictionary<string, object> args, string method)
    { if (method != PayMethod.Qr) args["p_method"] = method; return args; }

    public Task<AppResult<Payment>> AvailAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr) =>
        Safe.RunAsync(() => gw.RpcAsync<Payment>("avail_membership", WithMethod(WithCode(new() { ["p_package_id"] = packageId }, discountCode), method)));

    public Task<AppResult<Payment>> RenewAsync(int membershipId, string? discountCode = null, string method = PayMethod.Qr) =>
        Safe.RunAsync(() => gw.RpcAsync<Payment>("renew_membership", WithMethod(WithCode(new() { ["p_ump_id"] = membershipId }, discountCode), method)));

    public Task<AppResult<OnlineCheckout>> PayOnlineAsync(int paymentId) => OnlinePay.OpenAsync(gw, paymentId);
    public Task<AppResult<bool>> CheckOnlineAsync(int paymentId) => OnlinePay.CheckAsync(gw, paymentId);

    public async Task<AppResult<Payment>> PayAgainAsync(PaymentItem item, string? code = null)
    {
        if (item.PackageSnapshotId is { } package && item.Kind is PayKind.Membership or PayKind.Renewal)
            return await new SubscriptionService(gw).ExtendAsync(package, code);   // 0130, B1: server follows replacement chain as security definer
        if (item.ItemId is not { } id) return AppResult<Payment>.Fail(AppErrorKind.Validation, "not_found");
        return item.Kind switch
        {
            PayKind.Membership => await AvailAsync(id, code),
            PayKind.Renewal => await RenewAsync(id, code),
            _ => AppResult<Payment>.Fail(AppErrorKind.Validation, "not_found")
        };
    }

    public Task<AppResult<CoachAccessOffer?>> CoachAccessOfferAsync() => Safe.RunAsync(async () =>
        (await gw.RpcAsync<List<CoachAccessOffer>>("coach_access_offer", new()))?.FirstOrDefault());

    public Task<AppResult<Payment>> BuyCoachAccessAsync(string? discountCode = null) => Safe.RunAsync(() =>
        gw.RpcAsync<Payment>("buy_coach_access",
            string.IsNullOrWhiteSpace(discountCode) ? new() : new() { ["p_code"] = discountCode.Trim() }));

    public Task<AppResult<IReadOnlyDictionary<DateOnly, string>>> ClosedDaysAsync() => Safe.RunAsync(() => GymClosures.ByDayAsync(gw));
    public Task<AppResult<GymSettings>> GymAsync() => Safe.RunAsync(async () => (await gw.ListAsync<GymSettings>()).First());
}

// ===================== Payment verification and the counter (staff) =====================
public sealed record PaymentRow(Payment Payment, string Payer, string Item, bool HasProof, string? Booking = null)
{
    public string MethodText => Booking is not null && Payment.Method == PayMethod.Cash ? "At the desk" : PayMethod.Name(Payment.Method);   // 9.8: Void showed "cash" and "qr"
    public bool HasBooking => Booking is not null;
    /// <summary>F12: a "pay at the desk" booking is approved by taking the cash (into the cashier's drawer, 0050).</summary>
    public string ApproveText => PaysAtDesk ? "Take cash" : "Verify";
    bool PaysAtDesk => Booking is not null && Payment.Method == PayMethod.Cash;
    /// <summary>The reference column: a proof sent before the Xendit switch, the desk, or online (Xendit verifies those itself).</summary>
    public string ProofText => HasProof ? Payment.ReferenceNo ?? "" : PaysAtDesk ? "Pays at the desk" : "Paying online";
    public string WaitText => PaysAtDesk ? "Pays at the desk in cash" : "Waiting for the member to pay online";
    /// <summary>X11: the desk approves only a proof sent before the switch, or cash it takes; an online payment verifies itself.</summary>
    public bool CanApprove => HasProof || PaysAtDesk;
}

/// <summary>F13: a member's "I didn't use it", next to what the desk checks: their check-ins that day and the amenity's status now.</summary>
public sealed record ClaimRow(RefundRequest Claim, string Member, string Amenity, string When, string CheckIns, string AmenityNow)
{
    public string Price => $"₱{Claim.Amount:N2}";
    public string Sent => $"Sent {Claim.RequestedAt.ToUniversalTime().AddHours(8).ToString("ddd d MMM, h:mm tt", System.Globalization.CultureInfo.InvariantCulture)}";
}
/// <summary>Owner fixes 2026-11-19 Q5: a payment's status in plain words. The database values don't change.</summary>
public static class PaymentStatus
{
    public static string Text(string status, bool refunded = false) => status switch
    {
        Models.Status.Verified => refunded ? "Refunded" : "Paid",
        Models.Status.Rejected or Models.Status.Expired => "Failed",
        Models.Status.Voided => "Voided",   // only payments voided before 0100
        _ => status.Length == 0 ? "" : char.ToUpper(status[0]) + status[1..]
    };
}

/// <summary>Something the counter can take a payment for: a package, a membership to renew, or an amenity.</summary>
public sealed record CounterItem(int Id, string Label, decimal? Price = null, bool DayPass = false, bool IncludesCoach = false);   // 0098, 0125: Day pass packages

public interface IPaymentStaffService
{
    /// <summary>Owner fixes 2026-11-19 Q12 (0108): does this code work for this member and item? Kind is package, amenity or renewal (price shown only).</summary>
    Task<AppResult<CodeCheck>> CheckCodeAsync(string memberId, string code, string kind, int itemId, decimal? price = null);
    Task<AppResult<IReadOnlyList<PaymentRow>>> PendingAsync();
    /// <summary>Verified payments, newest first. For finding a payment to void.</summary>
    Task<AppResult<IReadOnlyList<PaymentRow>>> RecentAsync();
    Task<AppResult<bool>> VerifyAsync(int paymentId, bool approve, string? reason = null);
    /// <summary>F13 (0052): "I didn't use it" claims waiting for the front desk, oldest first.</summary>
    Task<AppResult<IReadOnlyList<ClaimRow>>> ClaimsAsync();
    Task<AppResult<bool>> DecideClaimAsync(int requestId, bool approve, string reason);
    /// <summary>Cash or GCash at the counter, verified at once. itemId is a package id, a membership id or an amenity id depending on kind.</summary>
    Task<AppResult<Payment>> RecordCounterAsync(string kind, string method, string? memberId, int? itemId, string? walkInName, string? referenceNo, string? discountCode);
    /// <summary>Cashier plan Y6 (0080): GCash or QR Ph at the desk, through Xendit: a pending online payment that Xendit's confirmation verifies.</summary>
    Task<AppResult<Payment>> StartDeskOnlineAsync(string kind, string? memberId, int? itemId, string? walkInName, string? discountCode);
    Task<AppResult<OnlineCheckout>> PayOnlineAsync(int paymentId);
    Task<AppResult<bool>> CheckOnlineAsync(int paymentId);
    /// <summary>One payment, fresh: the desk reads the receipt number once Xendit has confirmed it.</summary>
    Task<AppResult<Payment>> PaymentAsync(int paymentId);
    /// <summary>The newest desk online payment this cashier started that is still waiting for Xendit, so reopening the Cashier can check it.</summary>
    Task<AppResult<Payment?>> MyWaitingOnlineAsync();
    /// <summary>Cashier plan Y2: the desk registers a walk-in customer as a MEMBER (create-user lets the desk make members only). Returns their id.</summary>
    Task<AppResult<string>> RegisterMemberAsync(string firstName, string lastName, string email, string? phone, string password);
    /// <summary>What can be sold for a kind of payment. Renewals are the member's own memberships, so they need a memberId.</summary>
    Task<AppResult<IReadOnlyList<CounterItem>>> ItemsAsync(string kind, string? memberId);
    /// <summary>F2 (0050): the caller's open drawer shift (null when none) and the gym's opening float to start one with.</summary>
    Task<AppResult<(CashShift? Open, decimal DefaultFloat)>> ShiftAsync();
    Task<AppResult<CashShift>> OpenShiftAsync(decimal openingFloat, string? note);
    Task<AppResult<CashShift>> CloseShiftAsync(decimal counted, string? note, string? reason = null);
}

public sealed class PaymentStaffService(ISupabaseGateway gw) : IPaymentStaffService
{
    public Task<AppResult<CodeCheck>> CheckCodeAsync(string memberId, string code, string kind, int itemId, decimal? price = null) => Safe.RunAsync(async () =>
    {
        var args = new Dictionary<string, object> { ["p_member"] = memberId, ["p_code"] = code.Trim(), ["p_kind"] = kind, ["p_item"] = itemId };
        if (price is { } p) args["p_price"] = p;
        var rows = await gw.RpcAsync<List<CodeCheck>>("check_discount_code_for", args);
        return rows?.FirstOrDefault() ?? new CodeCheck { Reason = "invalid_code" };
    });

    public Task<AppResult<Payment>> StartDeskOnlineAsync(string kind, string? memberId, int? itemId, string? walkInName, string? discountCode) =>
        Safe.RunAsync(() =>
        {
            var args = new Dictionary<string, object> { ["p_kind"] = kind };
            if (memberId is not null) args["p_member_id"] = memberId;
            if (itemId is not null) args["p_item_id"] = itemId;
            if (!string.IsNullOrWhiteSpace(walkInName)) args["p_walk_in_name"] = walkInName.Trim();
            if (!string.IsNullOrWhiteSpace(discountCode)) args["p_discount_code"] = discountCode.Trim();
            return gw.RpcAsync<Payment>("start_desk_online_payment", args);
        });
    public Task<AppResult<OnlineCheckout>> PayOnlineAsync(int paymentId) => OnlinePay.OpenAsync(gw, paymentId);
    public Task<AppResult<bool>> CheckOnlineAsync(int paymentId) => OnlinePay.CheckAsync(gw, paymentId);
    public Task<AppResult<Payment>> PaymentAsync(int paymentId) => Safe.RunAsync(async () => (await gw.ListAsync<Payment>(p => p.Id == paymentId)).Single());

    public Task<AppResult<Payment?>> MyWaitingOnlineAsync() => Safe.RunAsync(async () =>
    {
        var me = gw.CurrentUserId;
        var now = DateTime.UtcNow;
        return (await gw.ListAsync<Payment>(p => p.Status == "pending" && p.Method == PayMethod.Qr && p.RecordedBy == me))
            .Where(p => p.ExpiresAt is { } e && ManilaTime.Utc(e) > now).OrderByDescending(p => p.CreatedAt).FirstOrDefault();
    });

    public async Task<AppResult<string>> RegisterMemberAsync(string firstName, string lastName, string email, string? phone, string password)
    {
        firstName = firstName.Trim().Normalize(); lastName = lastName.Trim().Normalize(); email = email.Trim();
        if (!AccountRules.IsName(firstName, lastName)) return AppResult<string>.Fail(AppErrorKind.Validation, "bad_name");
        if (!AccountRules.IsEmail(email)) return AppResult<string>.Fail(AppErrorKind.Validation, "bad_email");
        if (!AccountRules.IsPassword(password)) return AppResult<string>.Fail(AppErrorKind.Validation, "bad_password");
        return await Safe.RunAsync(async () =>
        {
            var body = new Dictionary<string, object> { ["first_name"] = firstName, ["last_name"] = lastName, ["name"] = NameParts.Join(firstName, lastName),
                ["email"] = email, ["role"] = "member", ["password"] = password };
            if (!string.IsNullOrWhiteSpace(phone)) body["phone"] = phone.Trim();
            return System.Text.Json.JsonDocument.Parse(await gw.InvokeFunctionAsync("create-user", body)).RootElement.GetProperty("id").GetString()!;
        });
    }

    async Task<IReadOnlyList<PaymentRow>> RowsAsync(IEnumerable<Payment> payments)
    {
        var namesTask = Directory_.UserNamesAsync(gw);
        var lookTask = PaymentLookup.LoadAsync(gw);
        var heldTask = gw.ListAsync<Reservation>(r => r.Status == "awaiting_payment");
        await Task.WhenAll(namesTask, lookTask, heldTask);
        var names = namesTask.Result;
        var look = lookTask.Result;
        var held = heldTask.Result.Where(r => r.PaymentId is not null).ToDictionary(r => r.PaymentId!.Value);
        return payments.Select(p => held.GetValueOrDefault(p.Id) is { } r
            // G13, G14 (0064): the down payment, the coach and what's left to pay at the desk
            ? new PaymentRow(p, names.GetValueOrDefault(p.MemberId ?? "", "Unknown"), $"{look.Item(p)} booking · down payment", p.ProofPath is not null,
                $"{BookingTime(r.StartsAt, r.EndsAt)} · {(r.CoachId is { } c ? $"with {names.GetValueOrDefault(c, "a coach")}" : "solo")} · balance ₱{r.Balance:N2} at the desk")
            : new PaymentRow(p, p.WalkInName ?? names.GetValueOrDefault(p.MemberId ?? "", "Unknown"), look.Item(p), p.ProofPath is not null)).ToList();
    }

    static string BookingTime(DateTime start, DateTime end)
    {
        var en = System.Globalization.CultureInfo.InvariantCulture;
        return $"{start.ToUniversalTime().AddHours(8).ToString("ddd d MMM, h:mm tt", en)} to {end.ToUniversalTime().AddHours(8).ToString("h:mm tt", en)}";
    }

    public Task<AppResult<IReadOnlyList<ClaimRow>>> ClaimsAsync() => Safe.RunAsync<IReadOnlyList<ClaimRow>>(async () =>
    {
        var claims = await gw.ListAsync<RefundRequest>(r => r.Kind == "no_show" && r.Status == "pending");
        if (claims.Count == 0) return [];
        var memberIds = claims.Select(c => c.MemberId).Distinct().ToList();
        var namesTask = Directory_.UserNamesAsync(gw);
        var bookingsTask = gw.ListAsync<Reservation>();
        var catalogTask = gw.ListAsync<Amenity>();
        var checkInsTask = gw.ListAsync<GymCheckIn>(x => memberIds.Contains(x.MemberId!));
        await Task.WhenAll(namesTask, bookingsTask, catalogTask, checkInsTask);
        var names = namesTask.Result;
        var bookings = bookingsTask.Result.ToDictionary(r => r.Id);
        var catalog = catalogTask.Result.ToDictionary(a => a.Id);
        var checkInsByMember = checkInsTask.Result.Where(x => x.MemberId is not null).ToLookup(x => x.MemberId!);
        var en = System.Globalization.CultureInfo.InvariantCulture;
        var rows = new List<ClaimRow>();
        foreach (var c in claims.OrderBy(c => c.RequestedAt))
        {
            var r = bookings.GetValueOrDefault(c.ReservationId ?? 0);
            if (r is null) continue;
            var dayStart = r.StartsAt.ToUniversalTime().AddHours(8).Date.AddHours(-8);   // midnight in Manila, as UTC
            var dayEnd = dayStart.AddDays(1);
            var ins = checkInsByMember[c.MemberId].Where(x => x.CheckedInAt.ToUniversalTime() >= dayStart && x.CheckedInAt.ToUniversalTime() < dayEnd)
                .OrderBy(x => x.CheckedInAt).Select(x => x.CheckedInAt.ToUniversalTime().AddHours(8).ToString("h:mm tt", en) + (x.Method == "qr" ? ", QR scan" : x.Method is null ? "" : ", by name")).ToList();
            var am = catalog.GetValueOrDefault(r.AmenityId);
            var now = am is null ? "" : am.Availability == "unavailable" ? $"Unavailable{(string.IsNullOrWhiteSpace(am.StatusReason) ? "" : $": {am.StatusReason}")}" : am.Availability == "in_use" ? "In use" : "Available";
            rows.Add(new ClaimRow(c, names.GetValueOrDefault(c.MemberId, "Member"), am?.Name ?? "Amenity", BookingTime(r.StartsAt, r.EndsAt),
                ins.Count == 0 ? "None that day" : string.Join("; ", ins), now));
        }
        return rows;
    });

    public async Task<AppResult<bool>> DecideClaimAsync(int requestId, bool approve, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () =>
        { await gw.RpcAsync("decide_amenity_refund", new() { ["p_request_id"] = requestId, ["p_approve"] = approve, ["p_reason"] = reason.Trim() }); return true; });
    }

    // cashier plan Y7: what waits for the desk is cash for a booking paid at the desk; online payments verify themselves (Xendit)
    public Task<AppResult<IReadOnlyList<PaymentRow>>> PendingAsync() => Safe.RunAsync(async () =>
        await RowsAsync((await gw.ListAsync<Payment>(p => p.Status == "pending" && p.Method == PayMethod.Cash)).OrderBy(p => p.CreatedAt)));

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


    public Task<AppResult<(CashShift? Open, decimal DefaultFloat)>> ShiftAsync() => Safe.RunAsync(async () =>
    {
        var shiftTask = gw.RpcAsync<List<CashShift>>("my_cash_shift", new());
        var settingsTask = gw.ListAsync<GymSettings>();
        await Task.WhenAll(shiftTask, settingsTask);
        return ((shiftTask.Result ?? []).FirstOrDefault(), settingsTask.Result.First().OpeningFloat);
    });

    public Task<AppResult<CashShift>> OpenShiftAsync(decimal openingFloat, string? note) => openingFloat < 0
        ? Task.FromResult(AppResult<CashShift>.Fail(AppErrorKind.Validation, "bad_input"))
        : Safe.RunAsync(async () =>
        {
            // item 1 (2026-11-12): the row open_cash_shift returns has no totals yet (null until closed, 0050), which a CashShift can't
            // hold; reading it failed after the shift was already open. my_cash_shift returns the same shift with its live totals.
            await gw.RpcAsync("open_cash_shift", new() { ["p_float"] = openingFloat, ["p_note"] = note?.Trim() ?? "" });
            return (await gw.RpcAsync<List<CashShift>>("my_cash_shift", new())).Single();
        });

    public Task<AppResult<CashShift>> CloseShiftAsync(decimal counted, string? note, string? reason = null) => counted < 0
        ? Task.FromResult(AppResult<CashShift>.Fail(AppErrorKind.Validation, "bad_input"))
        : Safe.RunAsync(async () =>
        {
            var closed = await gw.RpcAsync<CashShift>("close_cash_shift", new() { ["p_counted"] = counted, ["p_note"] = note?.Trim() ?? "", ["p_reason"] = reason?.Trim() ?? "" });
            closed.Difference = closed.Counted - closed.Expected;   // the table row has no difference column; the list function adds it
            closed.Shortage = closed.Difference is < 0 ? -closed.Difference.Value : 0m;
            closed.Overage = closed.Difference is > 0 ? closed.Difference.Value : 0m;
            return closed;
        });

    public Task<AppResult<IReadOnlyList<CounterItem>>> ItemsAsync(string kind, string? memberId) => Safe.RunAsync<IReadOnlyList<CounterItem>>(async () =>
    {
        switch (kind)
        {
            case PayKind.Membership:
                return (await gw.ListAsync<MembershipPackage>(p => p.IsActive == true)).Where(p => p.PublishedAt is not null)
                    .Select(p => new CounterItem(p.Id, p.Name, p.Price, p.IsDayPass, p.IncludesCoach)).ToList();
            case PayKind.Amenity:
                // G19 (0066): sessions only; an amenity still set up as a pass isn't sold until it has a session price
                return (await gw.ListAsync<Amenity>(a => a.IsActive == true)).Where(a => a.DurationDays is null).Select(a => new CounterItem(a.Id, a.Name, a.Price)).ToList();
            case PayKind.Renewal:
                var packagesTask = gw.ListAsync<MembershipPackage>();
                var umpsTask = gw.ListAsync<UserMembershipPackage>(m => m.MemberId == memberId && (m.Status == "active" || m.Status == "expired"));
                await Task.WhenAll(packagesTask, umpsTask);
                var packages = packagesTask.Result;
                return umpsTask.Result
                    .Select(m =>
                    {
                        var package = packages.FirstOrDefault(p => p.Id == m.MembershipPackageId);
                        return new CounterItem(m.Id, $"{package?.Name ?? "Membership"}  ({m.Status}, ends {ManilaTime.Of(m.EndsAt):d MMM yyyy})", package?.Price);
                    }).ToList();
            default:
                return [];
        }
    });
}

// ===================== Amenities =====================
/// <summary>One amenity in the catalogue and where the member stands with it: not bought, paid and waiting, or active.</summary>
public sealed record AmenityRow(Amenity Amenity, int? UserAmenityId, string? Status = null, DateTime? ExpiresAt = null, bool IsSession = false)
{
    public bool IsActive => Status == Models.Status.Active;
    public bool IsPending => Status == Models.Status.Pending;
}
public sealed record UsageRow(AmenityUsage Usage, string Member, string Amenity);

public sealed record AmenityCard(Amenity Amenity, bool Followed, bool CanFollow, string? PhotoUrl = null, string? PassStatus = null, DateTime? PassUntil = null, bool PassIsSession = false,
    IReadOnlyList<string>? Runners = null, bool RunsThis = false, bool IsCoach = false)
{
    // members only (CanFollow): what a pass costs, or where theirs stands; empty hides it for coaches and staff
    public string PassText => !CanFollow ? "" : PassStatus switch
    {
        Models.Status.Pending => "Waiting for your payment to be verified",
        Models.Status.Active when PassIsSession => "1 session ready to book",
        Models.Status.Active => PassUntil is { } until ? $"Pass active until {until.ToLocalTime():d MMM yyyy}" : "Pass active",
        _ => Amenity.DurationDays is not null ? "Not on sale right now" : $"₱{Amenity.Price:N2} per hour"   // G19 (0066): passes aren't sold
    };
    /// <summary>F12: a pass or a session already paid books without paying again.</summary>
    public bool HasPass => PassStatus == Models.Status.Active;
    public bool CanBook => CanFollow && !IsUnavailable && (HasPass || Amenity.DurationDays is null);

    /// <summary>F14: who runs it (other coaches, when a coach is looking). An amenity nobody runs works with any coach in the gym.</summary>
    public string RunText
    {
        get
        {
            var names = string.Join(", ", Runners ?? []);
            if (IsCoach) return RunsThis ? (names.Length > 0 ? $"Also assigned: {names}" : "Only you are assigned") : names.Length > 0 ? $"Assigned: {names}" : "Nobody yet: any coach in the gym counts";
            return names.Length > 0 ? $"Coaches: {names}" : CanFollow ? "Any coach in the gym" : "";
        }
    }

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
        && Amenity.ImagePath == o.Amenity.ImagePath && PhotoUrl == o.PhotoUrl && PassText == o.PassText && RunText == o.RunText && RunsThis == o.RunsThis;
}

/// <summary>What a status change did: whether the amenity really changed, and the members who were alerted.</summary>
public sealed record AmenityChange(bool Changed, IReadOnlyList<NotifiedMember> Told);

public sealed record AmenityBoard(IReadOnlyList<AmenityCard> Cards, bool CanFollow, bool CoachInGym = false);

public interface IAmenityService
{
    /// <summary>Every amenity that is switched on, with its live status and whether this member saved it. CanFollow is false for coaches and staff.</summary>
    Task<AppResult<AmenityBoard>> BoardAsync();
    Task<AppResult<bool>> SetFollowedAsync(int amenityId, bool follow);
    /// <summary>F14 (0052): a coach says they run an amenity, or no longer do.</summary>
    Task<AppResult<bool>> SetRunsAsync(int amenityId, bool on);
    /// <summary>Front desk and admin only. Says whether anything really changed (judged against the amenity as it is now, not an older card) and who was alerted.</summary>
    Task<AppResult<AmenityChange>> SetStatusAsync(int amenityId, string status, string? reason, DateTime? backAtUtc);

    Task<AppResult<IReadOnlyList<AmenityRow>>> CatalogAsync();
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
        var amenitiesTask = gw.ListAsync<Amenity>(a => a.IsActive == true);
        var savedTask = gw.ListAsync<AmenityFollow>(f => f.MemberId == uid);
        var memberTask = gw.ListAsync<Member>(m => m.UserId == uid);
        var coachTask = gw.ListAsync<Coach>(c => c.UserId == uid);
        var runsTask = gw.ListAsync<CoachAmenity>();
        var visitsTask = gw.ListAsync<CoachVisit>(v => v.CheckedOutAt == null);
        await Task.WhenAll(amenitiesTask, savedTask, memberTask, coachTask, runsTask, visitsTask);

        // owner, 2026-11-23: a draft is only for Admin Packages; staff can read drafts, so the board must not show them
        var amenities = amenitiesTask.Result.Where(a => a.State == CatalogState.Published).OrderBy(a => a.Name).ToList();
        var saved = savedTask.Result.Select(f => f.AmenityId).ToHashSet();
        var isMember = memberTask.Result.Count > 0;
        var isCoach = coachTask.Result.Count > 0;
        var runs = runsTask.Result;
        var coachInGym = visitsTask.Result.Count > 0;

        Dictionary<int, AmenityRow> passes = [];
        Dictionary<string, string> names = [];
        var passesTask = isMember ? CatalogRowsForAsync(uid!, amenities) : Task.FromResult<IReadOnlyList<AmenityRow>>([]);
        var namesTask = runs.Count == 0 ? Task.FromResult(new Dictionary<string, string>()) : Directory_.UserNamesAsync(gw);
        await Task.WhenAll(passesTask, namesTask);
        if (isMember) passes = passesTask.Result.ToDictionary(r => r.Amenity.Id);
        names = namesTask.Result;

        var cards = await Task.WhenAll(amenities.Select(async a =>
            new AmenityCard(a, saved.Contains(a.Id), isMember, (await Safe.RunAsync(() => photos.UrlAsync(a.ImagePath))).Value,
                passes.GetValueOrDefault(a.Id)?.Status, passes.GetValueOrDefault(a.Id)?.ExpiresAt, passes.GetValueOrDefault(a.Id)?.IsSession ?? false,
                runs.Where(r => r.AmenityId == a.Id && r.CoachId != uid).Select(r => names.GetValueOrDefault(r.CoachId, "A coach")).Order().ToList(),
                runs.Any(r => r.AmenityId == a.Id && r.CoachId == uid), isCoach)));
        return new AmenityBoard(cards, isMember, coachInGym);
    });

    public Task<AppResult<bool>> SetFollowedAsync(int amenityId, bool follow) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("set_amenity_followed", new() { ["p_amenity_id"] = amenityId, ["p_follow"] = follow }); return true; });

    public Task<AppResult<bool>> SetRunsAsync(int amenityId, bool on) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("set_runs_amenity", new() { ["p_amenity"] = amenityId, ["p_on"] = on }); return true; });

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
        var catalog = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        return await CatalogRowsForAsync(uid!, catalog);
    });

    async Task<IReadOnlyList<AmenityRow>> CatalogRowsForAsync(string? uid, IReadOnlyList<Amenity> catalog)
    {
        var now = DateTime.UtcNow;
        var resTask = gw.ListAsync<Reservation>(r => r.MemberId == uid);
        var uaTask = gw.ListAsync<UserAmenity>(a => a.MemberId == uid);
        await Task.WhenAll(resTask, uaTask);
        // F12: a session bought with a booking, or one still waiting for its payment, belongs to that booking, not to a pass
        var booked = resTask.Result.Where(r => r.PaymentId is not null).Select(r => r.UserAmenityId).ToHashSet();
        var mine = uaTask.Result.Where(a => !booked.Contains(a.Id) || (!a.IsSession && a.Status != Status.Pending)).ToList();
        // the latest entry per amenity tells where the member stands; an active one that ran out counts as expired
        return catalog.Select(a =>
        {
            var e = mine.Where(m => m.AmenityId == a.Id).OrderByDescending(m => m.AvailedAt).ThenByDescending(m => m.Id).FirstOrDefault();
            var status = e is null ? null : e.Status == Status.Active && e.ExpiresAt is { } ends && ManilaTime.Utc(ends) <= now ? Status.Expired : e.Status;
            return new AmenityRow(a, e?.Id, status, e?.ExpiresAt, e?.IsSession ?? false);
        }).ToList();
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
        if (pending.Count == 0) return [];
        var entsTask = gw.ListAsync<UserAmenity>();
        var amenitiesTask = gw.ListAsync<Amenity>();
        var namesTask = Directory_.UserNamesAsync(gw);
        await Task.WhenAll(entsTask, amenitiesTask, namesTask);
        var ents = entsTask.Result;
        var amenities = amenitiesTask.Result;
        var names = namesTask.Result;
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
/// <summary>InGym: the coach has an open physical visit (0032). The list shows only coaches in the gym, plus your own coach.</summary>
public sealed record CoachCard(Coach Coach, string Name, bool Hired, int? HireId, IReadOnlyList<ShiftPost>? Shifts = null, string? PhotoUrl = null, bool InGym = true, bool Taken = false, int? RequestId = null)
{
    /// <summary>G30 (0074): this member asked the coach and is waiting for an answer.</summary>
    public bool Requested => RequestId is not null;
    public bool ShowHire => !Hired && !Requested;
    public string WaitingText => $"Waiting for {Name.Split(' ')[0]} to accept.";
    /// <summary>G28 (0071): Taken = another member has an active hire with this coach. The "Hired" tag shows only to the member who hired
    /// (owner, 2026-11-16: it showed to members with no coach); hiring is unchanged.</summary>
    public string? HireBadge => Requested ? "requested" : Hired ? "hired" : null;
    public string Letters => Initials.From(Name);
    /// <summary>The coach paused hiring and this member has no hire to keep: shown grayed out, Hire disabled.</summary>
    public bool Closed => !Coach.IsAvailable && !Hired;
    /// <summary>G29 (0072): any coach taking members can be asked at any time, in the gym or not; not twice while waiting (G30).</summary>
    public bool CanHire => !Closed && !Requested;

    public bool SameAs(CoachCard o) => Name == o.Name && Hired == o.Hired && HireId == o.HireId && InGym == o.InGym && Taken == o.Taken && RequestId == o.RequestId && PhotoUrl == o.PhotoUrl && ShiftsText == o.ShiftsText
        && Coach.IsAvailable == o.Coach.IsAvailable && Coach.Bio == o.Coach.Bio && Coach.Specialty == o.Coach.Specialty;

    public string ShiftsText => Shifts is { Count: > 0 }
        ? string.Join("\n", Shifts.Take(3).Select(s => $"{Fmt.Range(s.Start, s.End)}{(string.IsNullOrWhiteSpace(s.Note) ? "" : "  " + s.Note)}"))
        : "No shifts posted yet.";
}

/// <summary>G30: a member waiting for the coach to accept their hire request.</summary>
public sealed record HireRequestRow(int HireId, string MemberId, string Name, string? PhotoUrl, DateTime AskedAt)
{
    public string Letters => Initials.From(Name);
    public string AskedDate => Fmt.Date(AskedAt);
    public string AskedText => $"Asked {AskedDate}";
}

public sealed record TraineeRow(int HireId, string CoachId, string MemberId, string Name, string NextText, int Completed)
{
    public string Letters => Initials.From(Name);
    public string CompletedText => Completed == 1 ? "1 session" : $"{Completed} sessions";
    /// <summary>The table already says "Next session"; the phone card keeps the "Next:" words.</summary>
    public string NextWhen => NextText.StartsWith("Next: ") ? NextText[6..] : NextText;
}

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
    /// <summary>G30: the coach answers a waiting hire request.</summary>
    Task<AppResult<bool>> AnswerHireAsync(int hireId, bool accept);
    /// <summary>G30: the member withdraws their own waiting request.</summary>
    Task<AppResult<bool>> CancelHireRequestAsync(int hireId);
    /// <summary>G30: the signed-in coach's waiting requests, oldest first.</summary>
    Task<AppResult<IReadOnlyList<HireRequestRow>>> HireRequestsAsync();
    Task<AppResult<Coach?>> MyCoachAsync();
    Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, bool available);
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
        var now = DateTime.UtcNow;
        var coachesTask = gw.ListAsync<Coach>();   // paused coaches stay listed, shown as closed
        var profilesTask = gw.ListAsync<PublicProfile>();
        var hiresTask = gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "active");
        var askedTask = gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "pending");
        var shiftsTask = gw.ListAsync<ShiftPost>(s => s.Status == "scheduled" && s.End > now);
        var visitsTask = gw.ListAsync<CoachVisit>(v => v.CheckedOutAt == null);
        var takenTask = gw.RpcAsync<List<string>>("taken_coaches");
        await Task.WhenAll(coachesTask, profilesTask, hiresTask, askedTask, shiftsTask, visitsTask, takenTask);

        var coaches = coachesTask.Result;
        var profiles = profilesTask.Result.ToDictionary(p => p.Id);
        var hires = hiresTask.Result;
        var asked = askedTask.Result;
        var shifts = shiftsTask.Result;
        var inGym = visitsTask.Result.Select(v => v.CoachId).ToHashSet();
        var taken = takenTask.Result.ToHashSet();
        // owner, 2026-11-15 (replaces D9's hiding): every active coach, in the gym first, then your own, then by next shift
        var shown = coaches.Where(c => profiles.GetValueOrDefault(c.UserId)?.IsActive != false)
            .OrderBy(c => !inGym.Contains(c.UserId)).ThenBy(c => !hires.Any(h => h.CoachId == c.UserId))
            .ThenBy(c => shifts.Where(s => s.CoachId == c.UserId).Select(s => (DateTime?)s.Start).Min() ?? DateTime.MaxValue);
        return await Task.WhenAll(shown.Select(async c =>
        {
            var p = profiles.GetValueOrDefault(c.UserId);
            var name = string.IsNullOrWhiteSpace(p?.FullName) ? "Coach" : p.FullName;
            var photo = (await Safe.RunAsync(() => photos.UrlAsync(p?.AvatarPath))).Value;
            return new CoachCard(c, name, hires.Any(h => h.CoachId == c.UserId), hires.FirstOrDefault(h => h.CoachId == c.UserId)?.Id,
                shifts.Where(s => s.CoachId == c.UserId).OrderBy(s => s.Start).ToList(), photo, inGym.Contains(c.UserId),
                taken.Contains(c.UserId) && !hires.Any(h => h.CoachId == c.UserId), asked.FirstOrDefault(h => h.CoachId == c.UserId)?.Id);
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
        var activeTask = gw.ListAsync<PublicProfile>(p => p.IsActive == true);
        var openTask = gw.ListAsync<CoachVisit>(v => v.CheckedOutAt == null);
        var coachesTask = gw.ListAsync<Coach>();
        await Task.WhenAll(activeTask, openTask, coachesTask);
        var active = activeTask.Result.ToDictionary(p => p.Id, p => p.FullName);
        var open = openTask.Result.ToDictionary(v => v.CoachId, v => v.CheckedInAt);
        return coachesTask.Result.Where(c => active.ContainsKey(c.UserId))
            .Select(c => new CoachPresenceRow(c.UserId, active[c.UserId], open.TryGetValue(c.UserId, out var t) ? t : null))
            .OrderByDescending(r => r.InGym).ThenBy(r => r.Name).ToList();
    });

    public Task<AppResult<bool>> HireAsync(string coachId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync<int>("hire_coach", new() { ["p_coach_id"] = coachId }); return true; });

    public Task<AppResult<bool>> EndHireAsync(int hireId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("end_hire", new() { ["p_hire_id"] = hireId }); return true; });

    public Task<AppResult<bool>> AnswerHireAsync(int hireId, bool accept) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("answer_hire", new() { ["p_hire_id"] = hireId, ["p_accept"] = accept }); return true; });

    public Task<AppResult<bool>> CancelHireRequestAsync(int hireId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("cancel_hire_request", new() { ["p_hire_id"] = hireId }); return true; });

    public Task<AppResult<IReadOnlyList<HireRequestRow>>> HireRequestsAsync() => Safe.RunAsync<IReadOnlyList<HireRequestRow>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var asks = await gw.ListAsync<CoachHire>(h => h.CoachId == uid && h.Status == "pending");
        if (asks.Count == 0) return [];
        var profiles = (await gw.ListAsync<PublicProfile>()).ToDictionary(p => p.Id);
        return await Task.WhenAll(asks.OrderBy(h => h.HiredAt).Select(async h =>
        {
            var p = profiles.GetValueOrDefault(h.MemberId);
            var photo = (await Safe.RunAsync(() => photos.UrlAsync(p?.AvatarPath))).Value;
            return new HireRequestRow(h.Id, h.MemberId, p?.FullName is { Length: > 0 } n ? n : "Member", photo, h.HiredAt);
        }));
    });

    public Task<AppResult<Coach?>> MyCoachAsync() => Safe.RunAsync<Coach?>(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<Coach>(c => c.UserId == uid)).FirstOrDefault(); });

    public Task<AppResult<bool>> UpdateProfileAsync(string? bio, string? specialty, bool available) =>
        Safe.RunAsync(async () =>
    {
        var c = (await MyCoachAsync()).Value ?? throw new Exception("not_found");
        c.Bio = bio; c.Specialty = specialty; c.IsAvailable = available;
        await gw.UpdateAsync(c);
        return true;
    });

    public Task<AppResult<IReadOnlyList<TraineeRow>>> TraineesAsync() => Safe.RunAsync<IReadOnlyList<TraineeRow>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var now = DateTime.UtcNow;
        var namesTask = Directory_.UserNamesAsync(gw);
        var sessionsTask = gw.ListAsync<TrainingSession>(s => s.CoachId == uid && s.Status != Status.Cancelled && (s.HeldUntil == null || s.HeldUntil > now));
        await Task.WhenAll(namesTask, sessionsTask);
        var names = namesTask.Result;
        var sessions = sessionsTask.Result.ToList();
        return sessions.Where(s => !string.IsNullOrEmpty(s.MemberId)).GroupBy(s => s.MemberId).Select(g =>
        {
            var latest = g.OrderByDescending(s => s.ScheduledStart).First();
            var next = g.Where(s => s.Status == Status.Scheduled && s.ScheduledStart > now).OrderBy(s => s.ScheduledStart).FirstOrDefault();
            var label = next is not null
                ? $"Next: {Fmt.Range(next.ScheduledStart, next.ScheduledEnd)}"
                : $"Last: {Fmt.Range(latest.ScheduledStart, latest.ScheduledEnd)}";
            return (Row: new TraineeRow(0, uid, g.Key, names.GetValueOrDefault(g.Key, "Member"), label, g.Count(s => s.Status == Status.Completed)),
                    Latest: latest.ScheduledStart);
        }).OrderByDescending(x => x.Latest).Select(x => x.Row).ToList();
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
    public string Title => !string.IsNullOrWhiteSpace(Session.Title) ? Session.Title
        : string.IsNullOrEmpty(Session.MemberId) ? "Walk-in session" : "Coach session";
    // the coach running a session on an amenity can add time until it ends (extend_reservation)
    public bool CanExtend => IsMine && ReservationId is not null && Session.Status == Status.Scheduled && Session.ScheduledEnd.ToUniversalTime() > DateTime.UtcNow;
    public bool IsWaitingForPayment => Session.Status == Status.Scheduled && Session.HeldUntil is not null;
    public bool HasQr => Session.Status == Status.Scheduled && Session.HeldUntil is null && !string.IsNullOrWhiteSpace(Session.SessionCode);
    public string StatusText => IsWaitingForPayment ? "Waiting for payment"
        : Session.Status == Status.Scheduled && Session.CoachArrivedAt is not null ? "In session"
        : Session.Status == Status.Scheduled && Session.CheckedInAt is not null ? "Checked in"
        : Session.Status;
}
public sealed record TodayCoachSession(
    [property: System.Text.Json.Serialization.JsonPropertyName("training_session_id")] int SessionId,
    [property: System.Text.Json.Serialization.JsonPropertyName("session_code")] string? SessionCode,
    [property: System.Text.Json.Serialization.JsonPropertyName("member_id")] string MemberId,
    [property: System.Text.Json.Serialization.JsonPropertyName("member_name")] string MemberName,
    [property: System.Text.Json.Serialization.JsonPropertyName("coach_id")] string CoachId,
    [property: System.Text.Json.Serialization.JsonPropertyName("coach_name")] string CoachName,
    [property: System.Text.Json.Serialization.JsonPropertyName("scheduled_start")] DateTime ScheduledStart,
    [property: System.Text.Json.Serialization.JsonPropertyName("scheduled_end")] DateTime ScheduledEnd,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("checked_in_at")] DateTime? CheckedInAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("coach_arrived_at")] DateTime? CoachArrivedAt,
    [property: System.Text.Json.Serialization.JsonPropertyName("held_until")] DateTime? HeldUntil)
{
    public string TimeRange => Fmt.Range(ScheduledStart, ScheduledEnd);
    public bool CanMarkCoachArrived => CheckedInAt is not null && CoachArrivedAt is null && Status == "Checked in";
    public bool CanCheckIn => CheckedInAt is null && HeldUntil is null && Status == "scheduled";
}
public sealed record FreeCoachSlot(
    [property: System.Text.Json.Serialization.JsonPropertyName("coach_id")] string CoachId,
    [property: System.Text.Json.Serialization.JsonPropertyName("full_name")] string FullName,
    [property: System.Text.Json.Serialization.JsonPropertyName("specialty")] string? Specialty,
    [property: System.Text.Json.Serialization.JsonPropertyName("bio")] string? Bio,
    [property: System.Text.Json.Serialization.JsonPropertyName("avatar_path")] string? AvatarPath,
    [property: System.Text.Json.Serialization.JsonPropertyName("free_hours")] int[] FreeHours,
    string? PhotoUrl = null)
{
    public string HoursSummary => FreeHours.Length == 0 ? "No free hours" : string.Join(", ", FreeHours.Select(h => DateTime.Today.AddHours(h).ToString("h tt")));
}
public sealed record BookCoachResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("training_session_id")] int TrainingSessionId,
    [property: System.Text.Json.Serialization.JsonPropertyName("payment_id")] int? PaymentId,
    [property: System.Text.Json.Serialization.JsonPropertyName("payment_code")] string? PaymentCode,
    [property: System.Text.Json.Serialization.JsonPropertyName("amount")] decimal Amount,
    [property: System.Text.Json.Serialization.JsonPropertyName("held_until")] DateTime? HeldUntil,
    [property: System.Text.Json.Serialization.JsonPropertyName("is_free")] bool IsFree);
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
    Task<AppResult<bool>> CancelSessionAsync(int sessionId, string? reason = null);
    /// <summary>The coach adds minutes (30-minute steps) to a session on an amenity; the session and its reservation move together.</summary>
    Task<AppResult<bool>> ExtendAsync(int reservationId, int minutes);
    /// <summary>The signed-in coach's sessions that start in [fromUtc, toUtc), oldest first, each with how it went (Mark is null until marked).</summary>
    Task<AppResult<IReadOnlyList<CalendarSession>>> CalendarAsync(DateTime fromUtc, DateTime toUtc);
    /// <summary>The signed-in coach's scheduled sessions from now on, nearest first.</summary>
    Task<AppResult<IReadOnlyList<CalendarSession>>> UpcomingAsync();
    string? PendingCoachId { get => null; set { } }
    Task<AppResult<IReadOnlyList<FreeCoachSlot>>> FreeCoachesAsync(DateOnly day) => Task.FromResult(AppResult<IReadOnlyList<FreeCoachSlot>>.Success([]));
    Task<AppResult<BookCoachResult>> BookCoachAsync(string coachId, DateTime startUtc, int hours, string? code = null) => Task.FromResult(AppResult<BookCoachResult>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<BookCoachResult>> DeskBookCoachAsync(string memberId, string coachId, DateTime startUtc, int hours, string method, string? code = null) => Task.FromResult(AppResult<BookCoachResult>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<int>> DeskBookWalkInCoachAsync(string coachId, DateTime startUtc, int hours, string? walkInName = null) => Task.FromResult(AppResult<int>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<TodayCoachSession>> ScanSessionAsync(string code) => Task.FromResult(AppResult<TodayCoachSession>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<bool>> MarkCoachArrivedAsync(int sessionId) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<IReadOnlyList<TodayCoachSession>>> TodayCoachSessionsAsync() => Task.FromResult(AppResult<IReadOnlyList<TodayCoachSession>>.Success([]));
    Task<AppResult<int>> ReportDamageAsync(int? sessionId, int? amenityId, string description, string? photoPath = null) => Task.FromResult(AppResult<int>.Fail(AppErrorKind.Unknown, "not_implemented"));
}

public sealed class SessionService(ISupabaseGateway gw) : ISessionService
{
    readonly LinkCache _photos = new(gw, "avatars", rememberMisses: true);

    public Task<AppResult<RequestsView>> RequestsAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var now = DateTime.UtcNow;
        var coachesTask = gw.ListAsync<Coach>();
        var namesTask = Directory_.UserNamesAsync(gw);
        var pendingTask = gw.ListAsync<TimeRequest>(r => r.Status == "pending");
        var proposedTask = gw.ListAsync<TimeRequest>(r => r.Status == "proposed");
        var amenitiesTask = gw.ListAsync<Amenity>();
        var hiresTask = gw.ListAsync<CoachHire>(h => h.MemberId == uid && h.Status == "active");
        var activeTask = gw.ListAsync<TrainingSession>(s => s.Status == Status.Scheduled);
        await Task.WhenAll(coachesTask, namesTask, pendingTask, proposedTask, amenitiesTask, hiresTask, activeTask);

        var coaches = coachesTask.Result;
        var iAmCoach = coaches.Any(c => c.UserId == uid);
        var names = namesTask.Result;
        var requests = pendingTask.Result.Concat(proposedTask.Result).ToList();
        var amenities = amenitiesTask.Result;
        var hires = hiresTask.Result;
        var active = activeTask.Result;
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

        IReadOnlyList<CoachCard> hired = coaches.Where(c => hires.Any(h => h.CoachId == c.UserId))
            .Select(c => new CoachCard(c, CoachName(c.UserId), true, null)).ToList();
        IReadOnlyList<RequestMember> trainees = [];
        if (iAmCoach)
        {
            var mineTask = gw.ListAsync<CoachHire>(h => h.CoachId == uid && h.Status == "active");
            var profilesTask = gw.ListAsync<PublicProfile>(p => p.IsActive == true);
            await Task.WhenAll(mineTask, profilesTask);
            var mine = mineTask.Result;
            trainees = profilesTask.Result.Where(p => mine.Any(h => h.MemberId == p.Id))
                .Select(p => new RequestMember(p.Id, MemberName(p.Id))).OrderBy(m => m.Name).ToList();
        }
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
        var namesTask = Directory_.UserNamesAsync(gw);
        var sessionsTask = gw.ListAsync<TrainingSession>();
        var resTask = gw.ListAsync<Reservation>(r => r.Status == "confirmed");
        await Task.WhenAll(namesTask, sessionsTask, resTask);
        var names = namesTask.Result;
        var sessions = sessionsTask.Result;
        var held = resTask.Result.Where(r => r.TrainingSessionId is not null).ToDictionary(r => r.TrainingSessionId!.Value);
        var amenities = held.Count == 0 ? [] : await gw.ListAsync<Amenity>();
        return sessions.OrderBy(s => s.ScheduledStart).Select(s =>
        {
            var isMine = s.CoachId == uid;
            var with = isMine ? s.MemberId : s.CoachId;
            var withName = isMine && string.IsNullOrEmpty(with) ? (s.WalkInName ?? "Walk-in") : names.GetValueOrDefault(with, "Unknown");
            var r = held.GetValueOrDefault(s.Id);
            return new SessionRow(s, withName, isMine, isMine && s.Status == Status.Scheduled, s.Status == Status.Scheduled,
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

    public Task<AppResult<bool>> CancelSessionAsync(int sessionId, string? reason = null) => Safe.RunAsync(async () =>
    {
        var args = new Dictionary<string, object> { ["p_session_id"] = sessionId };
        if (!string.IsNullOrWhiteSpace(reason)) args["p_reason"] = reason.Trim();
        await gw.RpcAsync("cancel_session", args);
        return true;
    });

    public Task<AppResult<bool>> SetSessionStatusAsync(int sessionId, string status) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("record_session_attendance", new() { ["p_session_id"] = sessionId, ["p_status"] = status == Status.NoShow ? "absent" : "present" });
        return true;
    });

    public Task<AppResult<IReadOnlyList<CalendarSession>>> CalendarAsync(DateTime fromUtc, DateTime toUtc) => Safe.RunAsync<IReadOnlyList<CalendarSession>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var sessionsTask = gw.ListAsync<TrainingSession>(s => s.CoachId == uid && s.ScheduledStart >= fromUtc && s.ScheduledStart < toUtc);
        var namesTask = Directory_.UserNamesAsync(gw);
        await Task.WhenAll(sessionsTask, namesTask);
        var sessions = sessionsTask.Result;
        var ids = sessions.Select(s => s.Id).ToList();
        var marks = (await gw.ListAsync<SessionAttendance>(a => ids.Contains(a.SessionId))).ToDictionary(a => a.SessionId, a => a.Status);   // asked even for an empty month: one tiny request, and the filter stays proven
        var names = namesTask.Result;
        return sessions.OrderBy(s => s.ScheduledStart)
            .Select(s => new CalendarSession(s, string.IsNullOrEmpty(s.MemberId) ? (s.WalkInName ?? "Walk-in") : names.GetValueOrDefault(s.MemberId, "Member"), marks.GetValueOrDefault(s.Id))).ToList();
    });

    public Task<AppResult<IReadOnlyList<CalendarSession>>> UpcomingAsync() => Safe.RunAsync<IReadOnlyList<CalendarSession>>(async () =>
    {
        var uid = gw.CurrentUserId ?? throw new Exception("not authenticated");
        var now = DateTime.UtcNow;
        var sessionsTask = gw.ListAsync<TrainingSession>(s => s.CoachId == uid && s.Status == Status.Scheduled && s.ScheduledStart >= now);
        var namesTask = Directory_.UserNamesAsync(gw);
        await Task.WhenAll(sessionsTask, namesTask);
        var names = namesTask.Result;
        return sessionsTask.Result.OrderBy(s => s.ScheduledStart)
            .Select(s => new CalendarSession(s, string.IsNullOrEmpty(s.MemberId) ? (s.WalkInName ?? "Walk-in") : names.GetValueOrDefault(s.MemberId, "Member"), null)).ToList();
    });

    public string? PendingCoachId { get; set; }

    public Task<AppResult<IReadOnlyList<FreeCoachSlot>>> FreeCoachesAsync(DateOnly day) => Safe.RunAsync<IReadOnlyList<FreeCoachSlot>>(async () =>
    {
        var rows = await gw.RpcAsync<List<FreeCoachSlot>>("free_coaches", new() { ["p_day"] = day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) }) ?? [];
        return await Task.WhenAll(rows.Select(async r => r with { PhotoUrl = (await Safe.RunAsync(() => _photos.UrlAsync(r.AvatarPath))).Value }));
    });

    public Task<AppResult<BookCoachResult>> BookCoachAsync(string coachId, DateTime startUtc, int hours, string? code = null) => Safe.RunAsync(() =>
        gw.RpcAsync<BookCoachResult>("book_coach_session", new()
        {
            ["p_coach"] = coachId,
            ["p_start"] = startUtc.ToString("O"),
            ["p_hours"] = hours,
            ["p_code"] = (string.IsNullOrWhiteSpace(code) ? null : code.Trim())!
        }));

    public Task<AppResult<int>> DeskBookWalkInCoachAsync(string coachId, DateTime startUtc, int hours, string? walkInName = null) => Safe.RunAsync(() =>
        gw.RpcAsync<int>("desk_book_walkin_coach_session", new()
        {
            ["p_coach"] = coachId,
            ["p_start"] = startUtc.ToString("O"),
            ["p_hours"] = hours,
            ["p_walk_in_name"] = (string.IsNullOrWhiteSpace(walkInName) ? null : walkInName.Trim())!
        }));

    public Task<AppResult<BookCoachResult>> DeskBookCoachAsync(string memberId, string coachId, DateTime startUtc, int hours, string method, string? code = null) => Safe.RunAsync(() =>
        gw.RpcAsync<BookCoachResult>("desk_book_coach_session", new()
        {
            ["p_member"] = memberId,
            ["p_coach"] = coachId,
            ["p_start"] = startUtc.ToString("O"),
            ["p_hours"] = hours,
            ["p_method"] = method,
            ["p_code"] = (string.IsNullOrWhiteSpace(code) ? null : code.Trim())!
        }));

    public Task<AppResult<TodayCoachSession>> ScanSessionAsync(string code) => Safe.RunAsync(async () =>
        (await gw.RpcAsync<List<TodayCoachSession>>("scan_session", new() { ["p_code"] = code.Trim() })).First());

    public Task<AppResult<bool>> MarkCoachArrivedAsync(int sessionId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("mark_coach_arrived", new() { ["p_session"] = sessionId }); return true; });

    public Task<AppResult<IReadOnlyList<TodayCoachSession>>> TodayCoachSessionsAsync() => Safe.RunAsync<IReadOnlyList<TodayCoachSession>>(async () =>
        await gw.RpcAsync<List<TodayCoachSession>>("today_coach_sessions"));

    public Task<AppResult<int>> ReportDamageAsync(int? sessionId, int? amenityId, string description, string? photoPath = null) => Safe.RunAsync(() =>
        gw.RpcAsync<int>("report_damage", new()
        {
            ["p_session_id"] = sessionId!,
            ["p_amenity_id"] = amenityId!,
            ["p_description"] = description.Trim(),
            ["p_photo_path"] = (string.IsNullOrWhiteSpace(photoPath) ? null : photoPath.Trim())!
        }));
}

// ===================== Attendance and check-ins =====================
/// <summary>
/// G10, G11 (Membership checking): why someone can't check in, in plain words, and the desk's next step for it. Status is what
/// scan_member says (active, pending, expired, inactive); an expired member with no end date never had a membership.
/// </summary>
public static class MemberCheck
{
    public static string Reason(string status, DateTime? ended) => status switch
    {
        "pending" => "Payment not verified yet",
        "inactive" => "Account deactivated",
        "expired" when ended is { } e => $"Expired on {ManilaTime.Of(e):d MMM}",
        "expired" => "No membership",
        _ => "",
    };

    /// <summary>The page staff open next, as (button text, Shell route); null when there is nothing the desk can do.</summary>
    public static (string Text, string Route)? NextStep(string status) => status switch
    {
        "pending" => ("Open Cashier", "cashier"),   // cashier plan Y7
        "expired" => ("Open Cashier", "cashier"),
        _ => null,
    };
}

/// <param name="Status">Online only: active, pending, expired or inactive (G10). Null on the saved list, which knows paid or not.</param>
public sealed record MemberHit(string MemberId, string Username, bool HasActiveMembership, DateTime? PaidUntil = null, string? AvatarPath = null,
    string? Status = null, DateTime? EndedAt = null, string? Phone = null, string? Email = null, bool IsDayPass = false, bool IsChosen = false)
{
    /// <summary>The Cashier's contact column: phone and email, so two people with the same name can be told apart.</summary>
    public string ContactText => string.Join(" · ", new[] { Phone, Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string PlanStatus => HasActiveMembership ? "active" : Status == "expired" && EndedAt is null ? "no_membership" : Status ?? "inactive";
    /// <summary>D3 (2026-11-20): status text for Cashier and Book for a customer.</summary>
    public string DeskStatusText => HasActiveMembership
        ? (IsDayPass ? "Day pass until midnight" : PaidUntil is { } until ? $"Member until {ManilaTime.Of(until):d MMM yyyy}" : "Active member")
        : "No membership";
    /// <summary>Forgot phone: what staff check before letting someone in without a QR; the reason when they can't (G10).</summary>
    public string PlanText => HasActiveMembership ? PaidUntil is { } until ? $"Paid until {ManilaTime.Of(until):d MMM yyyy}" : "Paid"
        : Status is { } s ? MemberCheck.Reason(s, EndedAt) : "No active membership";
    public bool CanCheckIn => HasActiveMembership;
    /// <summary>G11: the next step at the desk for someone who can't check in, e.g. Open Cashier.</summary>
    public string? NextStepText => CanCheckIn || Status is null ? null : MemberCheck.NextStep(Status)?.Text;
    public string? NextStepRoute => CanCheckIn || Status is null ? null : MemberCheck.NextStep(Status)?.Route;
    public bool HasNextStep => NextStepText is not null;
}

public interface IAttendanceService
{
    /// <summary>The coach records how a member did at a session: present, late, absent or excused.</summary>
    Task<AppResult<bool>> RecordAsync(int sessionId, string status);
    /// <param name="method">qr (the default: needs a pass this desk just scanned), manual or offline (0044, 0049).</param>
    /// <param name="pin">The member's desk PIN, for manual (F8). A wrong one fails as wrong_pin.</param>
    /// <param name="reason">Admin and System admin only: why they check in by name without the PIN.</param>
    /// <param name="proof">Method password (G12): the one-use proof from <see cref="VerifyPasswordAsync"/>.</param>
    Task<AppResult<bool>> CheckInAsync(string memberId, string? method = null, string? pin = null, string? reason = null, string? proof = null, string? idType = null);
    /// <summary>G12, login mode: the member's own account password, checked by the server; a proof for one check-in, or
    /// wrong_member_password / pin_locked. Needs the internet. The password is never kept.</summary>
    Task<AppResult<string>> VerifyPasswordAsync(string memberId, string password);
    /// <summary>F8: true for Admin and System admin, who may check in by name without the PIN, with a reason.</summary>
    Task<AppResult<bool>> CanSkipPinAsync();
    Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string usernameFragment);
    /// <summary>D3 (2026-11-20): re-read one member's live status when picked.</summary>
    Task<AppResult<MemberHit?>> MemberStatusAsync(string memberId);
    /// <summary>F6: a new one-use QR pass for the signed-in member, good for 5 minutes. The previous unused one stops working.</summary>
    Task<AppResult<MemberPass>> MyPassAsync();
    /// <summary>Front desk and admins: who a scanned code belongs to and their live status. Every scan is logged by the server.</summary>
    Task<AppResult<MemberScan>> ScanAsync(string code);
    /// <summary>Gym check-ins on one Manila day, earliest first.</summary>
    Task<AppResult<IReadOnlyList<CheckInRow>>> CheckInsOnAsync(DateOnly manilaDay);
}

/// <summary>What scan_member says about a code: result is active, pending, expired, inactive, old_code or not_found.</summary>
public sealed class MemberScan
{
    public string Result { get; set; } = "";
    public string? Member { get; set; }
    public string? Name { get; set; }
    public DateTime? Expires { get; set; }
    /// <summary>Set when this came from the saved list (OfflineDesk) rather than the server. It says how old that list is.</summary>
    public string? SavedNote { get; set; }
    /// <summary>G10: the member's photo path, looked up after the scan so the card can show their face.</summary>
    public string? AvatarPath { get; set; }

    public bool CanCheckIn => Result is "active" or "saved" && Member is not null;
    public bool IsSaved => Result.StartsWith("saved");
    /// <summary>G10: why this member can't check in, in plain words; blank when they can or the code named nobody.</summary>
    public string ReasonText => Member is null ? "" : MemberCheck.Reason(Result, Expires);
    /// <summary>G11: the desk's next step, e.g. Open Cashier.</summary>
    public string? NextStepText => MemberCheck.NextStep(Result)?.Text;
    public string? NextStepRoute => MemberCheck.NextStep(Result)?.Route;
    public bool HasNextStep => Member is not null && NextStepText is not null;
    /// <summary>G10: the card's main line: the reason for someone who can't check in, otherwise the status.</summary>
    public string HeadlineText => !CanCheckIn && ReasonText.Length > 0 ? ReasonText : StatusText;
    public bool NeedsAdmin => Member is not null && Result == "inactive";
    public string StatusText => Result switch
    {
        "saved" => $"Paid on the saved list. {SavedNote}",
        "saved_unpaid" => $"No paid term on the saved list. {SavedNote}",
        "saved_unknown" => $"Not on the saved list. {SavedNote}",
        "saved_stale" => "Can't check right now: the saved list is out of date or missing.",
        "saved_no_qr" => "Offline, so a QR code can't be checked. Find the member by name and ask for their desk PIN.",
        "old_code" => "Old code: it was already scanned or has run out. Ask the member to open their QR again.",
        "active" => "Active", "pending" => "Pending: a membership payment is waiting to be verified", "expired" => "Expired: no active membership",
        "inactive" => "Inactive: this account is archived", _ => "Not found: this code does not belong to any member"
    };
    public string ExpiryText => Expires is { } e ? (Result is "active" or "saved" ? "Membership ends " : "Membership ended ") + RevenuePdf.Manila(e) + " (Manila)" : IsSaved ? "" : "No end date";
}

/// <summary>G20 (0066): the IDs the desk may check a member in with; only the type is kept, never the number.</summary>
public static class IdTypes
{
    static readonly (string Code, string Label)[] All =
        [("drivers_license", "Driver's license"), ("national_id", "National ID"), ("passport", "Passport"), ("school_id", "School ID"), ("other", "Other")];
    public static IReadOnlyList<string> Labels { get; } = All.Select(t => t.Label).ToArray();
    public static string Label(string code) => All.FirstOrDefault(t => t.Code == code).Label ?? code;
    public static string? Code(string? label) => All.FirstOrDefault(t => t.Label == label).Code;
}

/// <summary>Owner, 2026-11-18: the payment behind a Day pass check-in. An anonymous Day pass is stored under the name "Day pass" (0081).</summary>
public sealed record DayPassDetails(string? Customer, string? ReceiptNo, decimal Amount, string Method, string Status, DateTime? PaidAt,
    int PaymentId = 0, string? RefundStatus = null, string? RefundDecision = null)
{
    public bool CanRequestRefund => Status == Models.Status.Verified && RefundStatus is null;
    public string RefundText => WalkInRefund.Text(RefundStatus, RefundDecision);
    public string Who => Customer ?? "No name given";
    public bool CanPrint => Status == Models.Status.Verified && ReceiptNo is not null;
    public string PaidText => Status == Models.Status.Verified ? $"Paid ₱{Amount:N2}" : $"₱{Amount:N2}, payment voided";
    public string MethodText => PayMethod.Name(Method);
}

/// <summary>0096: how a walk-in's refund request stands, in the desk's words.</summary>
public static class WalkInRefund
{
    public static string Text(string? status, string? decision) => status switch
    {
        "pending" => "Refund requested. Waiting for the System admin.",
        "approved" => "Refund approved. The System admin pays it back.",
        "paid" => "Refund repaid.",
        "rejected" => $"Refund rejected: {decision}",
        _ => ""
    };
}

/// <summary>A Day pass receipt as a one-page A4 PDF the desk prints (Check-in page).</summary>
public static class DayPassReceiptPdf
{
    public static byte[] Build(CheckInRow row, DayPassDetails pass, DateTime printedUtc, byte[] font) =>
        RevenuePdf.Document("Day pass receipt", $"Receipt {pass.ReceiptNo}", font,
            [($"Receipt {pass.ReceiptNo}", RevenuePdf.Kind.Strong), ("", RevenuePdf.Kind.Gap),
             ($"Customer: {pass.Who}", RevenuePdf.Kind.Text), ($"Checked in: {RevenuePdf.Manila(row.At)} (Asia/Manila)", RevenuePdf.Kind.Text),
             ("", RevenuePdf.Kind.Gap), ($"Printed {RevenuePdf.Manila(printedUtc)} (Asia/Manila)", RevenuePdf.Kind.Body)],
            [("Item", 120, false), ("Paid on", 140, false), ("Method", 100, false), ("Amount", 100, true)],
            [["Day pass", pass.PaidAt is { } at ? RevenuePdf.Manila(at) : "", pass.MethodText, $"₱{pass.Amount:N2}"]],
            ["Total paid", "", "", $"₱{pass.Amount:N2}"], "", []);
}

public sealed record CheckInRow(string Name, DateTime At, string? Method = null, string? By = null, string? Reason = null, string? IdType = null, DayPassDetails? DayPass = null)
{
    public bool HasDayPass => DayPass is not null;
    public string TimeText => At.AddHours(8).ToString("h:mm tt", CultureInfo.InvariantCulture);   // Manila has no daylight saving
    /// <summary>How it came in (0044); blank for check-ins recorded before that. A reason means an Admin skipped the PIN (F8).</summary>
    public string HowText => (Method, IdType) switch
    {
        ("offline", { } t) => $"ID checked ({IdTypes.Label(t)}), sent later",
        (_, { } t) => $"ID checked ({IdTypes.Label(t)})",
        ("qr", _) => "QR", ("manual", _) when Reason is not null => "By name, no PIN", ("manual", _) => "By name", ("offline", _) => "Sent later", _ => ""
    };
    /// <summary>F8: who recorded it, and why an Admin skipped the PIN; blank for check-ins from before this was kept.</summary>
    public string ByText => By is null ? "" : Reason is null ? $"By {By}" : $"By {By}. Reason: {Reason}";
    /// <summary>F19: the second line of a row on the Check-in page, "QR · By Marco Santos".</summary>
    public string HowByText => string.Join(" · ", new[] { HowText, ByText }.Where(t => t.Length > 0));
    /// <summary>F19: came in during the last minute; the Check-in page tints it.</summary>
    public bool IsNew { get; init; }
}

public sealed class AttendanceService(ISupabaseGateway gw, TimeProvider? time = null) : IAttendanceService
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public Task<AppResult<bool>> RecordAsync(int sessionId, string status) => Safe.RunAsync(async () =>
    {
        await gw.RpcAsync("record_session_attendance", new() { ["p_session_id"] = sessionId, ["p_status"] = status });
        return true;
    });

    public async Task<AppResult<bool>> CheckInAsync(string memberId, string? method = null, string? pin = null, string? reason = null, string? proof = null, string? idType = null)
    {
        Dictionary<string, object> args = new() { ["p_member_id"] = memberId };
        if (method is not null) args["p_method"] = method;
        if (proof is not null) args["p_proof"] = proof;
        if (idType is not null) args["p_id_type"] = idType;
        if (!string.IsNullOrEmpty(pin)) args["p_pin"] = pin;
        if (!string.IsNullOrWhiteSpace(reason)) args["p_reason"] = reason.Trim();
        var id = await Safe.RunAsync(() => gw.RpcAsync<int?>("record_check_in", args));
        // 0049: a wrong PIN comes back as no id, not an error, so the server can keep its count of wrong tries
        return !id.Ok ? AppResult<bool>.Fail(id.Error, id.Message) : id.Value is null ? AppResult<bool>.Fail(AppErrorKind.Validation, "wrong_pin") : AppResult<bool>.Success(true);
    }

    public async Task<AppResult<string>> VerifyPasswordAsync(string memberId, string password)
    {
        var result = await Safe.RunAsync(async () =>
            System.Text.Json.JsonDocument.Parse(await gw.InvokeFunctionAsync("verify-member-password", new() { ["member_id"] = memberId, ["password"] = password }))
                .RootElement.GetProperty("proof").GetString()!);
        // "wrong_password" is also the code for a staff member's own wrong password in Settings; this one is the member's
        return result.Message == "wrong_password" ? AppResult<string>.Fail(AppErrorKind.Validation, "wrong_member_password") : result;
    }

    public Task<AppResult<bool>> CanSkipPinAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var mineTask = gw.ListAsync<UserRole>(u => u.UserId == uid);
        var rolesTask = gw.ListAsync<Role>();
        await Task.WhenAll(mineTask, rolesTask);
        var mine = mineTask.Result.Select(u => u.RoleId).ToHashSet();
        return rolesTask.Result.Any(r => r.Name is "admin" or "subadmin" && mine.Contains(r.RoleId));
    });

    public Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string fragment) => Safe.RunAsync<IReadOnlyList<MemberHit>>(() =>
    {
        // cashier plan Y1: name, sign-in email (the username) or phone; a phone matches on its digits, so "0917-555" finds "0917 555 0142"
        static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
        var text = fragment.Trim(); var digits = Digits(text);
        return BuildHitsAsync((p, name) => name.Contains(text, StringComparison.OrdinalIgnoreCase) || p.Username.Contains(text, StringComparison.OrdinalIgnoreCase)
            || (digits.Length >= 3 && Digits(p.Phone).Contains(digits)));
    });

    public Task<AppResult<MemberHit?>> MemberStatusAsync(string memberId) => Safe.RunAsync<MemberHit?>(async () =>
        (await BuildHitsAsync((p, _) => p.Id == memberId)).FirstOrDefault());

    async Task<IReadOnlyList<MemberHit>> BuildHitsAsync(Func<Profile, string, bool> matches)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var membersTask = gw.ListAsync<Member>();
        var profilesTask = gw.ListAsync<Profile>();
        var dayPassTask = gw.ListAsync<MembershipPackage>(p => p.IsDayPass);
        var membershipsTask = gw.ListAsync<UserMembershipPackage>();
        var pendingTask = gw.ListAsync<Payment>(p => p.Status == Status.Pending);
        await Task.WhenAll(membersTask, profilesTask, dayPassTask, membershipsTask, pendingTask);

        var members = membersTask.Result;
        var profiles = profilesTask.Result;
        var dayPassIds = dayPassTask.Result.Select(p => p.Id).ToHashSet();
        var memberships = membershipsTask.Result;
        var active = memberships.Where(m => m.Status == "active" && ManilaTime.Utc(m.EndsAt) > now)
            .GroupBy(m => m.MemberId)
            .ToDictionary(g => g.Key, g => (Until: g.Max(m => ManilaTime.Utc(m.EndsAt)), IsDayPass: g.Any(m => dayPassIds.Contains(m.MembershipPackageId))));
        var ended = memberships.Where(m => ManilaTime.Utc(m.EndsAt) <= now).GroupBy(m => m.MemberId).ToDictionary(g => g.Key, g => g.Max(m => ManilaTime.Utc(m.EndsAt)));
        var waiting = pendingTask.Result.Where(p => p.Kind is PayKind.Membership or PayKind.Renewal).Select(p => p.MemberId).ToHashSet();
        // G10: the same order scan_member uses: deactivated, active, waiting for a payment, otherwise expired (or never had one)
        string StatusOf(Profile p) => !p.IsActive ? "inactive" : active.ContainsKey(p.Id) ? "active" : waiting.Contains(p.Id) ? "pending" : "expired";
        return members.Join(profiles, m => m.UserId, p => p.Id, (m, p) => (p, Name: string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName))
            .Where(x => matches(x.p, x.Name))
            .Select(x =>
            {
                var has = active.TryGetValue(x.p.Id, out var term) && x.p.IsActive;
                return new MemberHit(x.p.Id, x.Name, has, has ? term.Until : null, x.p.AvatarPath, StatusOf(x.p), ended.GetValueOrDefault(x.p.Id), x.p.Phone, x.p.Username, has && term.IsDayPass);
            }).ToList();
    }

    public Task<AppResult<MemberPass>> MyPassAsync() =>
        Safe.RunAsync(async () => (await gw.RpcAsync<List<MemberPass>>("issue_member_pass", new())).Single());

    public Task<AppResult<MemberScan>> ScanAsync(string code) => Safe.RunAsync(async () =>
    {
        var scan = await gw.RpcAsync<MemberScan>("scan_member", new() { ["p_code"] = code.Trim() });
        if (scan.Member is { } id) scan.AvatarPath = (await gw.ListAsync<Profile>(p => p.Id == id)).FirstOrDefault()?.AvatarPath;
        return scan;
    });

    public Task<AppResult<IReadOnlyList<CheckInRow>>> CheckInsOnAsync(DateOnly manilaDay) => Safe.RunAsync<IReadOnlyList<CheckInRow>>(async () =>
    {
        var from = DateTime.SpecifyKind(manilaDay.ToDateTime(TimeOnly.MinValue).AddHours(-8), DateTimeKind.Utc);   // Manila has no daylight saving
        var to = from.AddDays(1);
        var rowsTask = gw.ListAsync<GymCheckIn>(c => c.CheckedInAt >= from && c.CheckedInAt < to);
        var namesTask = Directory_.UserNamesAsync(gw);
        await Task.WhenAll(rowsTask, namesTask);
        var rows = rowsTask.Result;
        var names = namesTask.Result;
        var passIds = rows.Where(c => c.MemberId is null).Select(c => c.PaymentId).OfType<int>().ToList();
        Dictionary<int, Payment> passes = [];
        Dictionary<int, RefundRequest> asked = [];
        if (passIds.Count > 0)
        {
            var passesTask = gw.ListAsync<Payment>(p => passIds.Contains(p.Id));
            var askedTask = gw.ListAsync<RefundRequest>(r => r.Kind == "day_pass");
            await Task.WhenAll(passesTask, askedTask);
            passes = passesTask.Result.ToDictionary(p => p.Id);
            if (passes.Count > 0) asked = askedTask.Result.ToDictionary(r => r.PaymentId);
        }
        DayPassDetails? PassOf(GymCheckIn c) => c.MemberId is null && c.PaymentId is { } id && passes.GetValueOrDefault(id) is { } p
            ? new(p.WalkInName is null or "Day pass" ? null : p.WalkInName, p.ReceiptNo, p.Amount, p.Method, p.Status, p.VerifiedAt?.ToUniversalTime(),
                  p.Id, asked.GetValueOrDefault(id)?.Status, asked.GetValueOrDefault(id)?.DecisionReason) : null;
        return rows.OrderBy(c => c.CheckedInAt)
            .Select(c => new CheckInRow(c.MemberId is { } m ? names.GetValueOrDefault(m, "Member") : "Day pass", c.CheckedInAt.ToUniversalTime(), c.Method,   // the server's time may arrive as local
                c.RecordedBy is { } by ? names.GetValueOrDefault(by) : null, c.Reason, c.IdType, PassOf(c))).ToList();
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
/// <summary>Username is the name shown; Email is the address the account signs in with (profiles.username follows it, 0035).</summary>
public sealed record UserRow(string UserId, string Username, bool IsActive, IReadOnlyList<string> Roles, string FirstName = "", string LastName = "", string Email = "")
{
    public string RoleText => string.Join(", ", Roles.Select(RoleTitle));   // 9.8: titles in lists, as the audit log shows them
    /// <summary>What a role is called on screen, the one place for it (D17): the code admin is the System admin, subadmin is the Admin,
    /// and (D28, 9.8) employee is the Front desk, as the menu calls it.</summary>
    public static string RoleName(string role) => role switch { "admin" => "system admin", "subadmin" => "admin", "employee" => "front desk", _ => role };
    /// <summary>RoleName with a capital, for titles and buttons.</summary>
    public static string RoleTitle(string role) => RoleName(role) is var n ? char.ToUpperInvariant(n[0]) + n[1..] : "";
    /// <summary>
    /// The role code for a name in the Add user picker, or null. Explicit on purpose (D17): lower-casing "Admin" would ask for
    /// admin, the System admin role. The System admin is never offered here, and create-user refuses it anyway.
    /// </summary>
    public static string? RoleCode(string shown) => shown switch { "Member" => "member", "Coach" => "coach", "Front desk" => "employee", "Admin" => "subadmin", _ => null };
    public bool IsCoach => Roles.Contains("coach");
    public bool IsEmployee => Roles.Contains("employee");
    public string CoachActionText => IsCoach ? "Remove coach" : "Make coach";
    public string EmployeeActionText => IsEmployee ? "Remove employee" : "Make employee";
    public bool IsSubAdmin => Roles.Contains("subadmin");
    /// <summary>Fixed from the app (0037): no role changes, no deactivating, no deleting.</summary>
    public bool IsSuperAdmin => Roles.Contains("admin");
    public string SubAdminActionText => IsSubAdmin ? "Remove admin" : "Make admin";
    /// <summary>Front desk, sub-admin or super admin: only the super admin manages these accounts (D15).</summary>
    /// <summary>Item 3 (0059): a first name and no last name, from before both were required. Corrected, never guessed.</summary>
    public bool NameIncomplete => string.IsNullOrWhiteSpace(LastName);
    public bool IsStaff => Roles.Any(r => r is "admin" or "subadmin" or "employee");
    public string ActiveActionText => IsActive ? "Deactivate" : "Reactivate";
    public string ActiveStatus => RecordStatus.Of(IsActive);
}
/// <summary>G3 (0060): what a code fits, in words. One place for staff and members, so both read the same.</summary>
public static class DiscountFits
{
    public static string Text(bool allPackages, IEnumerable<int> packageIds, bool allAmenities, IEnumerable<int> amenityIds,
        IReadOnlyDictionary<int, string> packageNames, IReadOnlyDictionary<int, string> amenityNames, bool forCoach = false)
    {
        static string? Part(bool all, string allText, IEnumerable<int> ids, IReadOnlyDictionary<int, string> names) =>
            all ? allText : ids.Select(names.GetValueOrDefault).OfType<string>().ToList() is { Count: > 0 } n ? string.Join(", ", n) : null;
        var parts = new[] { Part(allPackages, "All packages", packageIds, packageNames), Part(allAmenities, "All amenities", amenityIds, amenityNames), forCoach ? "Coach add-on" : null }.OfType<string>().ToList();
        return parts.Count > 0 ? string.Join(" · ", parts) : "Nothing on sale now";
    }

    /// <summary>"For all packages", "For Monthly, Annual": the same words running on from "For".</summary>
    public static string For(string fitsText) => "For " + fitsText.Replace("All packages", "all packages").Replace("All amenities", "all amenities");
}

public sealed record DiscountRow(Discount Discount, string State, long Used = 0, long Pending = 0, string FitsText = "")
{
    /// <summary>The chip word (D13): an expired code is Inactive; a code turned off for now says so (G1).</summary>
    public string StatusText => State == "active" && Discount.Paused ? "Turned off" : RecordStatus.Of(State == "active");
    // used = verified + voided; pending payments hold a place too
    /// <summary>The last day it works: ExpiresAt is the midnight that ends that day, so showing it directly reads one day late.</summary>
    public DateTime LastDay => Discount.ExpiresAt.AddTicks(-1);
    public string Usage => (Discount.MaxUses is { } max ? $"{Used} of {max} used" : $"{Used} used, no limit") + (Pending > 0 ? $", {Pending} pending" : "");
    /// <summary>G1: offered only while no payment holds or used a place; the server also counts rejected and expired ones (discount_used).</summary>
    public bool CanDelete => Used + Pending == 0;
    public string ForText => DiscountFits.For(FitsText);
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
    /// <summary>Fixes a mistake in a locked name. System admin only (0040), with a reason that goes in the audit log.</summary>
    Task<AppResult<bool>> CorrectNameAsync(string userId, string firstName, string lastName, string reason);
    Task<AppResult<bool>> DeletePackageAsync(MembershipPackage package);
    Task<AppResult<bool>> DeleteAmenityAsync(Amenity amenity);
    Task<AppResult<IReadOnlyList<DiscountRow>>> DiscountsAsync();
    /// <summary>G2/G3 (0060): makes the code and what it fits in one step. A code can't be edited afterwards.</summary>
    Task<AppResult<int>> CreateDiscountAsync(Discount discount, IReadOnlyList<DiscountItem> items);
    /// <summary>G1: turns a live code off for now, or back on.</summary>
    Task<AppResult<bool>> SetDiscountPausedAsync(Discount discount, bool paused);
    /// <summary>Moves the code to the Archive (a System admin, D15); payments keep pointing at it.</summary>
    Task<AppResult<bool>> ArchiveDiscountAsync(Discount discount);
    /// <summary>G1: removes a code no payment ever named (a System admin).</summary>
    Task<AppResult<bool>> DeleteDiscountAsync(Discount discount);
    Task<AppResult<bool>> SavePackageAsync(MembershipPackage package);
    /// <summary>G6 (0061): puts a draft on sale once its required fields are filled; a package version archives the one it replaces. kind: "plan" or "amenity".</summary>
    Task<AppResult<bool>> PublishAsync(string kind, int id);
    /// <summary>G9: copies a published package into a draft that replaces it when published. Returns the draft's id.</summary>
    Task<AppResult<int>> NewPackageVersionAsync(int packageId);
    /// <summary>G6: removes a draft for real; anything published is archived instead.</summary>
    Task<AppResult<bool>> DeleteDraftAsync(string kind, int id);
    Task<AppResult<bool>> SaveAmenityAsync(Amenity amenity);
    Task<AppResult<string>> SetAmenityPhotoAsync(int amenityId, string fileName, byte[] data);
    /// <summary>Phase 4: a draft package's photo (a published one changes only through a new version).</summary>
    Task<AppResult<string>> SetPackagePhotoAsync(int packageId, string fileName, byte[] data);
    Task<AppResult<string?>> CatalogPhotoUrlAsync(string path);
    Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync();
    Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync();
    Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to);
    /// <summary>0058: one row per Manila day (net, payments, refunds, check-ins) for the Windows Revenue page.</summary>
    Task<AppResult<IReadOnlyList<RevenueDay>>> RevenueDaysAsync(DateOnly from, DateOnly to);
    /// <summary>Every receipt and approved refund in the range, all pages, for the PDF export.</summary>
    Task<AppResult<IReadOnlyList<RevenueLine>>> RevenueLinesAsync(DateOnly from, DateOnly to);
    /// <summary>F2 (0050): every drawer shift opened in the range (Manila days), newest first. Admin and System admin.</summary>
    Task<AppResult<IReadOnlyList<CashShift>>> CashShiftsAsync(DateOnly from, DateOnly to);
    /// <summary>D8 (0117): append-only supervisor investigation note on a closed cash shift.</summary>
    Task<AppResult<CashShiftNote>> AddCashShiftNoteAsync(int shiftId, string note, bool resolve = false);
    /// <summary>One page of the audit log, newest first, filtered on the server (0045). Page 0 is the newest.</summary>
    Task<AppResult<AuditPage>> AuditPageAsync(AuditFilter filter, int page);
    /// <summary>Every row the filter matches, for the CSV export, read in pages of 1000.</summary>
    Task<AppResult<IReadOnlyList<AuditRecord>>> AuditExportAsync(AuditFilter filter);
    Task<AppResult<GymSettings>> GymSettingsAsync();
    /// <summary>Saves the gym name, QR account name and walk-in price, and uploads a new payment QR image when one is given.</summary>
    Task<AppResult<bool>> SaveGymSettingsAsync(GymSettings settings);
    /// <summary>G16: every closed day, the latest first (past ones stay as history).</summary>
    Task<AppResult<IReadOnlyList<GymClosure>>> ClosuresAsync();
    /// <summary>G18: what closing the day would cancel.</summary>
    Task<AppResult<ClosurePreview>> ClosurePreviewAsync(DateOnly day);
    Task<AppResult<bool>> CloseDayAsync(DateOnly day, string reason);
    Task<AppResult<bool>> ReopenDayAsync(DateOnly day);
    Task<AppResult<IReadOnlyList<DamageReportRow>>> DamageReportsAsync() => Task.FromResult(AppResult<IReadOnlyList<DamageReportRow>>.Success([]));
    Task<AppResult<bool>> ReviewDamageAsync(int reportId, string status, string? note = null, bool setMaintenance = false) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<IReadOnlyList<CoachHoursRow>>> CoachHoursAsync(DateOnly from, DateOnly to) => Task.FromResult(AppResult<IReadOnlyList<CoachHoursRow>>.Success([]));
    Task<AppResult<IReadOnlyList<StaffSalary>>> PayrollStaffAsync() => Task.FromResult(AppResult<IReadOnlyList<StaffSalary>>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<IReadOnlyList<RoleSalary>>> PayrollRoleSalariesAsync() => Task.FromResult(AppResult<IReadOnlyList<RoleSalary>>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<bool>> SetRoleSalaryAsync(string role, decimal amount, string? payType = null) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<bool>> SetStaffSalaryAsync(string userId, decimal amount, string? payType = null) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<bool>> UseRoleSalaryAsync(string userId) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<PayrollPeriod>> PayrollPeriodAsync(DateOnly startsOn) => Task.FromResult(AppResult<PayrollPeriod>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<IReadOnlyList<PayrollLine>>> PayrollLinesAsync(int periodId, string userId) => Task.FromResult(AppResult<IReadOnlyList<PayrollLine>>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<PayrollLine>> AddPayrollLineAsync(int periodId, string userId, string kind, decimal amount, string note, string category = "other") => Task.FromResult(AppResult<PayrollLine>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<IReadOnlyList<PayrollTimeRecord>>> PayrollRecordsAsync(int periodId, string userId) => Task.FromResult(AppResult<IReadOnlyList<PayrollTimeRecord>>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<bool>> ReviewPayrollTimeAsync(int periodId, string source, long sourceId, string status, string? reason = null) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<int>> ApproveAllPayrollTimeAsync(int periodId, string? userId) => Task.FromResult(AppResult<int>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<IReadOnlyList<PayrollHourAdjustment>>> PayrollAdjustmentsAsync(int periodId, string userId) => Task.FromResult(AppResult<IReadOnlyList<PayrollHourAdjustment>>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<PayrollHourAdjustment>> AddPayrollHoursAsync(int periodId, string userId, decimal quantity, string note) => Task.FromResult(AppResult<PayrollHourAdjustment>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<bool>> RemovePayrollHoursAsync(int adjustmentId) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<bool>> RemovePayrollLineAsync(int lineId) => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Unknown, "not_implemented"));
    Task<AppResult<PayrollPeriod>> MarkPayrollPaidAsync(int periodId) => Task.FromResult(AppResult<PayrollPeriod>.Fail(AppErrorKind.Unknown, "not_implemented"));
}

public sealed record CoachHoursRow(
    [property: System.Text.Json.Serialization.JsonPropertyName("coach_id")] string CoachId,
    [property: System.Text.Json.Serialization.JsonPropertyName("coach_name")] string CoachName,
    [property: System.Text.Json.Serialization.JsonPropertyName("completed_sessions")] int CompletedSessions,
    [property: System.Text.Json.Serialization.JsonPropertyName("completed_hours")] decimal CompletedHours,
    [property: System.Text.Json.Serialization.JsonPropertyName("no_shows")] int NoShows,
    [property: System.Text.Json.Serialization.JsonPropertyName("coach_cancelled")] int CoachCancelled)
{
    public string HoursText => $"{CompletedHours:0.##} hrs";
}

public sealed record DamageReportRow(DamageReport Report, string ReporterName, string? CoachName, string? AmenityName)
{
    public bool CanReview => Report.Status != "closed";
    public bool HasAmenity => Report.AmenityId is not null;
    public string Subtitle => string.Join("  ·  ", new[] { AmenityName, CoachName is null ? null : $"Coach {CoachName}", $"Reported by {ReporterName}" }.OfType<string>());
}

public sealed class AdminService(ISupabaseGateway gw) : IAdminService
{
    public Task<AppResult<IReadOnlyList<UserRow>>> UsersAsync() => Safe.RunAsync<IReadOnlyList<UserRow>>(async () =>
    {
        var profilesTask = gw.ListAsync<Profile>();
        var rolesTask = gw.ListAsync<Role>();
        var linksTask = gw.ListAsync<UserRole>();
        await Task.WhenAll(profilesTask, rolesTask, linksTask);
        var roles = rolesTask.Result;
        var linksByUser = linksTask.Result.ToLookup(l => l.UserId);
        return profilesTask.Result.Where(p => p.DeletedAt is null).Select(p => new UserRow(p.Id, string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName, p.IsActive,
            linksByUser[p.Id].Select(l => roles.First(r => r.RoleId == l.RoleId).Name).OrderBy(n => n).ToList(), p.FirstName, p.LastName, p.Username)).ToList();
    });

    public Task<AppResult<bool>> IsSuperAdminAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var mineTask = gw.ListAsync<UserRole>(u => u.UserId == uid);
        var rolesTask = gw.ListAsync<Role>();
        await Task.WhenAll(mineTask, rolesTask);
        var mine = mineTask.Result.Select(u => u.RoleId).ToHashSet();
        return rolesTask.Result.Any(r => r.Name == "admin" && mine.Contains(r.RoleId));
    });

    public Task<AppResult<bool>> AssignRoleAsync(string userId, string role) => Rpc("assign_role", new() { ["p_user"] = userId, ["p_role"] = role });
    public Task<AppResult<bool>> RevokeRoleAsync(string userId, string role) => Rpc("revoke_role", new() { ["p_user"] = userId, ["p_role"] = role });

    public Task<AppResult<IReadOnlyList<DamageReportRow>>> DamageReportsAsync() => Safe.RunAsync<IReadOnlyList<DamageReportRow>>(async () =>
    {
        var reportsTask = gw.ListAsync<DamageReport>();
        var namesTask = Directory_.UserNamesAsync(gw);
        var amenitiesTask = gw.ListAsync<Amenity>();
        await Task.WhenAll(reportsTask, namesTask, amenitiesTask);
        var reports = reportsTask.Result.OrderByDescending(r => r.CreatedAt).ToList();
        var names = namesTask.Result;
        var amenities = amenitiesTask.Result.ToDictionary(a => a.Id, a => a.Name);
        return reports.Select(r => new DamageReportRow(
            r,
            names.GetValueOrDefault(r.ReportedBy, "Staff"),
            r.CoachId is null ? null : names.GetValueOrDefault(r.CoachId, "Coach"),
            r.AmenityId is { } aid ? amenities.GetValueOrDefault(aid, "Amenity") : null)).ToList();
    });

    public Task<AppResult<bool>> ReviewDamageAsync(int reportId, string status, string? note = null, bool setMaintenance = false) => Rpc("review_damage", new()
    {
        ["p_report_id"] = reportId,
        ["p_status"] = status,
        ["p_note"] = (string.IsNullOrWhiteSpace(note) ? null : note.Trim())!,
        ["p_set_maintenance"] = setMaintenance
    });

    public Task<AppResult<IReadOnlyList<CoachHoursRow>>> CoachHoursAsync(DateOnly from, DateOnly to) => Safe.RunAsync<IReadOnlyList<CoachHoursRow>>(async () =>
        await gw.RpcAsync<List<CoachHoursRow>>("coach_hours_report", new() { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd") }));

    public Task<AppResult<IReadOnlyList<StaffSalary>>> PayrollStaffAsync() => Safe.RunAsync<IReadOnlyList<StaffSalary>>(async () =>
        await gw.RpcAsync<List<StaffSalary>>("payroll_staff", new()) ?? []);

    public Task<AppResult<IReadOnlyList<RoleSalary>>> PayrollRoleSalariesAsync() => Safe.RunAsync<IReadOnlyList<RoleSalary>>(async () =>
        await gw.RpcAsync<List<RoleSalary>>("payroll_role_salaries", new()) ?? []);

    public Task<AppResult<bool>> SetRoleSalaryAsync(string role, decimal amount, string? payType = null) =>
        Rpc("set_role_salary", new() { ["p_role"] = role, ["p_amount"] = amount, ["p_pay_type"] = payType! });

    public Task<AppResult<bool>> SetStaffSalaryAsync(string userId, decimal amount, string? payType = null) =>
        Rpc("set_staff_salary", new() { ["p_user"] = userId, ["p_amount"] = amount, ["p_pay_type"] = payType! });

    public Task<AppResult<bool>> UseRoleSalaryAsync(string userId) => Rpc("use_role_salary", new() { ["p_user"] = userId });

    public Task<AppResult<IReadOnlyList<PayrollTimeRecord>>> PayrollRecordsAsync(int periodId, string userId) => Safe.RunAsync<IReadOnlyList<PayrollTimeRecord>>(async () =>
        await gw.RpcAsync<List<PayrollTimeRecord>>("payroll_records_for", new() { ["p_period"] = periodId, ["p_user"] = userId }) ?? []);

    public Task<AppResult<bool>> ReviewPayrollTimeAsync(int periodId, string source, long sourceId, string status, string? reason = null) =>
        Rpc("payroll_review_time", new() { ["p_period"] = periodId, ["p_source"] = source, ["p_source_id"] = sourceId, ["p_status"] = status, ["p_reason"] = reason?.Trim()! });

    public Task<AppResult<int>> ApproveAllPayrollTimeAsync(int periodId, string? userId) => Safe.RunAsync(() =>
        gw.RpcAsync<int>("payroll_approve_all", new() { ["p_period"] = periodId, ["p_user"] = userId! }));

    public Task<AppResult<IReadOnlyList<PayrollHourAdjustment>>> PayrollAdjustmentsAsync(int periodId, string userId) => Safe.RunAsync<IReadOnlyList<PayrollHourAdjustment>>(async () =>
        await gw.RpcAsync<List<PayrollHourAdjustment>>("payroll_adjustments_for", new() { ["p_period"] = periodId, ["p_user"] = userId }) ?? []);

    public Task<AppResult<PayrollHourAdjustment>> AddPayrollHoursAsync(int periodId, string userId, decimal quantity, string note) => Safe.RunAsync(() =>
        gw.RpcAsync<PayrollHourAdjustment>("payroll_add_hours", new() { ["p_period"] = periodId, ["p_user"] = userId, ["p_quantity"] = quantity, ["p_note"] = note.Trim() }));

    public Task<AppResult<bool>> RemovePayrollHoursAsync(int adjustmentId) => Rpc("payroll_remove_hours", new() { ["p_adjustment"] = adjustmentId });

    public Task<AppResult<PayrollPeriod>> PayrollPeriodAsync(DateOnly startsOn) => Safe.RunAsync(async () =>
        await gw.RpcAsync<PayrollPeriod>("payroll_period", new() { ["p_starts_on"] = startsOn.ToString("yyyy-MM-dd") }));

    public Task<AppResult<IReadOnlyList<PayrollLine>>> PayrollLinesAsync(int periodId, string userId) => Safe.RunAsync<IReadOnlyList<PayrollLine>>(async () =>
        await gw.RpcAsync<List<PayrollLine>>("payroll_lines_for", new() { ["p_period"] = periodId, ["p_user"] = userId }) ?? []);

    public Task<AppResult<PayrollLine>> AddPayrollLineAsync(int periodId, string userId, string kind, decimal amount, string note, string category = "other") => Safe.RunAsync(() =>
        gw.RpcAsync<PayrollLine>("payroll_add_line", new() { ["p_period"] = periodId, ["p_user"] = userId, ["p_kind"] = kind, ["p_amount"] = amount, ["p_note"] = note.Trim(), ["p_category"] = category }));

    public Task<AppResult<bool>> RemovePayrollLineAsync(int lineId) =>
        Rpc("payroll_remove_line", new() { ["p_line"] = lineId });

    public Task<AppResult<PayrollPeriod>> MarkPayrollPaidAsync(int periodId) => Safe.RunAsync(() =>
        gw.RpcAsync<PayrollPeriod>("payroll_mark_paid", new() { ["p_period"] = periodId }));
    public Task<AppResult<bool>> SetActiveAsync(string userId, bool active) => Rpc("set_user_active", new() { ["p_user"] = userId, ["p_active"] = active });

    // accounts are made by an Edge Function: it holds the service-role key, which the app never has
    public async Task<AppResult<bool>> CreateUserAsync(string firstName, string lastName, string email, string role, string password)
    {
        firstName = firstName.Trim().Normalize(); lastName = lastName.Trim().Normalize(); email = email.Trim();
        if (role is not ("member" or "coach" or "employee" or "subadmin")) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        if (!AccountRules.IsName(firstName, lastName)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name");   // the database refuses it too (0012, 0035)
        if (!AccountRules.IsEmail(email)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_email");
        if (!AccountRules.IsPassword(password)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_password");   // create-user checks it too
        return await Safe.RunAsync(async () =>
        {
            // name goes along for a create-user function deployed before first/last name existed
            await gw.InvokeFunctionAsync("create-user", new()
            {
                ["first_name"] = firstName, ["last_name"] = lastName, ["name"] = NameParts.Join(firstName, lastName),
                ["email"] = email, ["role"] = role, ["password"] = password
            });
            return true;
        });
    }

    public Task<AppResult<bool>> CorrectNameAsync(string userId, string firstName, string lastName, string reason)
    {
        firstName = firstName.Trim().Normalize(); lastName = lastName.Trim().Normalize(); reason = reason.Trim();
        if (!AccountRules.IsName(firstName, lastName)) return Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name"));
        if (reason.Length is < 1 or > 500) return Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Validation, "bad_correction_reason"));
        return Rpc("correct_name", new() { ["p_user"] = userId, ["p_first"] = firstName, ["p_last"] = lastName, ["p_reason"] = reason });
    }

    public async Task<AppResult<bool>> ResetPasswordAsync(string userId, string password)
    {
        if (!AccountRules.IsPassword(password)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_password");   // reset-password checks it too
        return await Safe.RunAsync(async () => { await gw.InvokeFunctionAsync("reset-password", new() { ["user_id"] = userId, ["password"] = password }); return true; });
    }

    public Task<AppResult<bool>> DeleteAmenityAsync(Amenity a) => Rpc("archive_catalog_record", new() { ["p_type"] = "amenity", ["p_id"] = a.Id, ["p_reason"] = "Deactivated amenity" });
    public Task<AppResult<bool>> DeletePackageAsync(MembershipPackage p) => Rpc("archive_catalog_record", new() { ["p_type"] = "plan", ["p_id"] = p.Id, ["p_reason"] = "Deactivated gym plan" });

    public Task<AppResult<IReadOnlyList<DiscountRow>>> DiscountsAsync() => Safe.RunAsync<IReadOnlyList<DiscountRow>>(async () =>
    {
        var now = DateTime.UtcNow;
        var usesTask = gw.RpcAsync<List<DiscountUse>>("discount_usage", new());
        var itemsTask = gw.ListAsync<DiscountItem>();
        var packagesTask = gw.ListAsync<MembershipPackage>();
        var amenitiesTask = gw.ListAsync<Amenity>();
        var discountsTask = gw.ListAsync<Discount>();
        await Task.WhenAll(usesTask, itemsTask, packagesTask, amenitiesTask, discountsTask);
        var uses = (usesTask.Result ?? []).ToDictionary(u => u.Id);
        var items = itemsTask.Result.ToLookup(i => i.DiscountId);
        var packages = packagesTask.Result.ToDictionary(p => p.Id, p => p.Name);
        var amenities = amenitiesTask.Result.ToDictionary(a => a.Id, a => a.Name);
        string Fits(int id)
        {
            var mine = items[id].ToList();
            bool All(string kind) => mine.Any(i => i.Kind == kind && i.ItemId is null);
            IEnumerable<int> Ids(string kind) => mine.Where(i => i.Kind == kind && i.ItemId is not null).Select(i => i.ItemId!.Value);
            return DiscountFits.Text(All("package"), Ids("package"), All("amenity"), Ids("amenity"), packages, amenities, All("coach"));
        }
        return discountsTask.Result.OrderByDescending(d => d.ExpiresAt)
            .Select(d => new DiscountRow(d, !d.IsActive ? "inactive" : ManilaTime.Utc(d.ExpiresAt) <= now ? "expired" : "active",
                uses.GetValueOrDefault(d.Id)?.Used ?? 0, uses.GetValueOrDefault(d.Id)?.Pending ?? 0, Fits(d.Id))).ToList();
    });

    public async Task<AppResult<int>> CreateDiscountAsync(Discount d, IReadOnlyList<DiscountItem> items)
    {
        var code = d.Code.Trim().ToUpperInvariant();
        if (code.Length == 0 || d.Percent is < 1 or > 100) return AppResult<int>.Fail(AppErrorKind.Validation, "bad_discount");
        if (d.MaxUses is not > 0) return AppResult<int>.Fail(AppErrorKind.Validation, "limit_required");
        if (items.Count == 0) return AppResult<int>.Fail(AppErrorKind.Validation, "no_items");
        var list = items.Select(i => new Dictionary<string, object?> { ["kind"] = i.Kind, ["id"] = i.ItemId }).ToList();
        return await Safe.RunAsync(async () => await gw.RpcAsync<int>("create_discount", new()
        {
            ["p_code"] = code, ["p_percent"] = d.Percent, ["p_expires_at"] = d.ExpiresAt.ToUniversalTime().ToString("O"), ["p_max_uses"] = d.MaxUses,
            ["p_show"] = d.ShowOnPackages, ["p_items"] = list
        }));
    }

    public Task<AppResult<bool>> SetDiscountPausedAsync(Discount d, bool paused) => Rpc("set_discount_paused", new() { ["p_id"] = d.Id, ["p_paused"] = paused });
    public Task<AppResult<bool>> ArchiveDiscountAsync(Discount d) => Rpc("archive_catalog_record", new() { ["p_type"] = "code", ["p_id"] = d.Id, ["p_reason"] = "Archived code" });
    public Task<AppResult<bool>> DeleteDiscountAsync(Discount d) => Rpc("delete_discount", new() { ["p_id"] = d.Id });

    public Task<AppResult<bool>> SavePackageAsync(MembershipPackage p)
    {
        if (p.IncludesCoach) p.CoachAddonPrice = null;
        if (p.CoachAddonPrice is <= 0) return Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input"));
        return Safe.RunAsync(async () => { if (p.Id == 0) p.Id = (await gw.InsertAsync(p)).Id; else await gw.UpdateAsync(p); return true; });
    }
    public Task<AppResult<bool>> PublishAsync(string kind, int id) => Rpc("publish_catalog_item", new() { ["p_kind"] = kind, ["p_id"] = id });
    public Task<AppResult<int>> NewPackageVersionAsync(int packageId) => Safe.RunAsync(() => gw.RpcAsync<int>("new_package_version", new() { ["p_id"] = packageId }));
    public Task<AppResult<bool>> DeleteDraftAsync(string kind, int id) => Rpc("delete_catalog_item", new() { ["p_kind"] = kind, ["p_id"] = id });
    public Task<AppResult<bool>> SaveAmenityAsync(Amenity a) =>
        Safe.RunAsync(async () => { if (a.Id == 0) a.Id = (await gw.InsertAsync(a)).Id; else await gw.UpdateAsync(a); return true; });

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

    public Task<AppResult<string>> SetPackagePhotoAsync(int packageId, string fileName, byte[] data)
    {
        if (!ProfileService.IsAcceptableImage(fileName, data)) return Task.FromResult(AppResult<string>.Fail(AppErrorKind.Validation, "bad_image"));
        return Safe.RunAsync(async () =>
        {
            var path = await gw.UploadAsync("gym-assets", $"packages/{packageId}/{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}", data);
            await gw.UpdateWhereAsync<MembershipPackage, string?>(p => p.Id == packageId, p => p.ImagePath, path);
            return path;
        });
    }

    public Task<AppResult<string?>> CatalogPhotoUrlAsync(string path) => Safe.RunAsync(() => gw.SignedUrlAsync("gym-assets", path));

    public Task<AppResult<IReadOnlyList<MembershipPackage>>> AllPackagesAsync() => Safe.RunAsync(() => gw.ListAsync<MembershipPackage>());
    public Task<AppResult<IReadOnlyList<Amenity>>> AllAmenitiesAsync() => Safe.RunAsync(() => gw.ListAsync<Amenity>());

    public async Task<AppResult<IReadOnlyList<RevenueRow>>> RevenueAsync(DateOnly from, DateOnly to)
    {
        if (to < from) return AppResult<IReadOnlyList<RevenueRow>>.Fail(AppErrorKind.Validation, "to before from");
        return await Safe.RunAsync(async () => (IReadOnlyList<RevenueRow>)(await gw.RpcAsync<List<RevenueRow>>("revenue_summary",
            new() { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd") }) ?? []));
    }

    public async Task<AppResult<IReadOnlyList<RevenueDay>>> RevenueDaysAsync(DateOnly from, DateOnly to)
    {
        if (to < from) return AppResult<IReadOnlyList<RevenueDay>>.Fail(AppErrorKind.Validation, "to before from");
        return await Safe.RunAsync(async () => (IReadOnlyList<RevenueDay>)(await gw.RpcAsync<List<RevenueDay>>("revenue_days",
            new() { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd") }) ?? []));
    }

    public async Task<AppResult<IReadOnlyList<CashShift>>> CashShiftsAsync(DateOnly from, DateOnly to)
    {
        if (to < from) return AppResult<IReadOnlyList<CashShift>>.Fail(AppErrorKind.Validation, "to before from");
        return await Safe.RunAsync(async () => (IReadOnlyList<CashShift>)(await gw.RpcAsync<List<CashShift>>("cash_shifts_list",
            new() { ["p_from"] = from.ToString("yyyy-MM-dd"), ["p_to"] = to.ToString("yyyy-MM-dd") }) ?? []));
    }

    public Task<AppResult<CashShiftNote>> AddCashShiftNoteAsync(int shiftId, string note, bool resolve = false) =>
        Safe.RunAsync(() => gw.RpcAsync<CashShiftNote>("add_cash_shift_note",
            new() { ["p_shift"] = shiftId, ["p_note"] = note.Trim(), ["p_resolve"] = resolve }));

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

    public const int AuditPageSize = 10;   // item 14

    public Task<AppResult<AuditPage>> AuditPageAsync(AuditFilter filter, int page) => Safe.RunAsync(async () =>
    {
        var rows = await AuditRowsAsync(filter, page * AuditPageSize, AuditPageSize + 1);
        return new AuditPage(rows.Take(AuditPageSize).ToList(), rows.Count > AuditPageSize);
    });

    public Task<AppResult<IReadOnlyList<AuditRecord>>> AuditExportAsync(AuditFilter filter) => Safe.RunAsync<IReadOnlyList<AuditRecord>>(async () =>
    {
        // ponytail: whole export held in memory; stream it if the log ever reaches hundreds of thousands of rows
        var all = new List<AuditRecord>();
        while (true)
        {
            var rows = await AuditRowsAsync(filter, all.Count, 1000);
            all.AddRange(rows);
            if (rows.Count < 1000) return all;
        }
    });

    async Task<List<AuditRecord>> AuditRowsAsync(AuditFilter f, int offset, int limit)
    {
        Dictionary<string, object> args = new()
        {
            ["p_from"] = f.From?.ToString("yyyy-MM-dd")!, ["p_to"] = f.To?.ToString("yyyy-MM-dd")!, ["p_user"] = f.User?.Trim() ?? "",
            ["p_area"] = f.Area ?? "", ["p_entity"] = f.Entity ?? "", ["p_offset"] = offset, ["p_limit"] = limit
        };
        if (f.Money is { } money) args["p_money"] = money;   // 0086
        if (!string.IsNullOrWhiteSpace(f.Customer)) args["p_customer"] = f.Customer.Trim();   // 0101: Transactions' own filters
        if (!string.IsNullOrEmpty(f.What)) args["p_what"] = f.What;
        if (!string.IsNullOrEmpty(f.PaidWith)) args["p_paid_with"] = f.PaidWith;
        if (!string.IsNullOrEmpty(f.Status)) args["p_status"] = f.Status;
        return await gw.RpcAsync<List<AuditRecord>>("audit_records", args) ?? [];
    }

    public Task<AppResult<GymSettings>> GymSettingsAsync() => Safe.RunAsync(async () => (await gw.ListAsync<GymSettings>()).First());

    public async Task<AppResult<bool>> SaveGymSettingsAsync(GymSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.GymName)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        if (s.WalkInPrice <= 0) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_walk_in_price");   // D4: ₱0 could never be paid
        if (s.DownPaymentPercent is < 1 or > 100) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_down_payment");   // G13, as 0064's check
        if (s.MaxCoachSessionsPerDay < 1 || s.MaxUpcomingCoachSessions < 1 || s.MaxCoachHoursPerDay is < 1)
            return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        static bool OnGrid(TimeSpan t) => t.Seconds == 0 && t.Minutes is 0 or 30 && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
        if (!OnGrid(s.OpensAt) || !OnGrid(s.ClosesAt) || s.ClosesAt <= s.OpensAt) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_hours");
        return await Safe.RunAsync(async () =>
        {
            await gw.UpdateAsync(s);
            return true;
        });
    }

    static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    public Task<AppResult<IReadOnlyList<GymClosure>>> ClosuresAsync() => Safe.RunAsync<IReadOnlyList<GymClosure>>(async () =>
        (await gw.ListAsync<GymClosure>()).OrderByDescending(c => c.ClosedOn).ToList());
    public Task<AppResult<ClosurePreview>> ClosurePreviewAsync(DateOnly day) => Safe.RunAsync(() => gw.RpcAsync<ClosurePreview>("closure_preview", new() { ["p_date"] = Day(day) }));
    public Task<AppResult<bool>> CloseDayAsync(DateOnly day, string reason) => Rpc("close_gym_day", new() { ["p_date"] = Day(day), ["p_reason"] = reason.Trim() });
    public Task<AppResult<bool>> ReopenDayAsync(DateOnly day) => Rpc("reopen_gym_day", new() { ["p_date"] = Day(day) });

    Task<AppResult<bool>> Rpc(string fn, Dictionary<string, object> args) =>
        Safe.RunAsync(async () => { await gw.RpcAsync(fn, args); return true; });
}

// ===================== Settings =====================
public interface ISettingsService
{
    Task<AppResult<UserSettings>> LoadAsync();
    Task<AppResult<bool>> SaveThemeAsync(string theme);
    /// <summary>True when the signed-in user is a member (only members have a desk PIN).</summary>
    Task<AppResult<bool>> IsMemberAsync();
    /// <summary>F8: the 4-digit PIN the member types at the front desk when they check in without their phone.</summary>
    Task<AppResult<bool>> SetDeskPinAsync(string pin);
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

    public Task<AppResult<bool>> IsMemberAsync() => Safe.RunAsync(async () =>
    { var uid = gw.CurrentUserId; return (await gw.ListAsync<Member>(m => m.UserId == uid)).Count > 0; });

    public async Task<AppResult<bool>> SetDeskPinAsync(string pin)
    {
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_pin");   // the server checks too
        return await Safe.RunAsync(async () => { await gw.RpcAsync("set_desk_pin", new() { ["p_pin"] = pin }); return true; });
    }
}
