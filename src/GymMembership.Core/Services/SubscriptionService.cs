using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>G23: the one sentence said before anyone pays for a membership (Packages, the Buy confirm, the cashier confirm).</summary>
public static class RefundRules
{
    public const string Line = "Refunds: only in a term's first 7 days, for the days not used.";
}

/// <summary>G22 (owner, 2026-11-15; 0067 does the same in the database): a membership refund is the unused days of what was paid, counted
/// when staff approve. G23 (0068): a member may ask only in the term's first 7 days. A started day counts as used.</summary>
public static class RefundMath
{
    public static int DaysUsed(DateTime starts, int total, DateTime at) =>
        at <= starts ? 0 : Math.Min(total, (int)Math.Ceiling((at - starts).TotalSeconds / 86400));
    public static decimal Due(decimal paid, int total, DateTime starts, DateTime at) =>
        Math.Round(paid * (total - DaysUsed(starts, total, at)) / total, 2, MidpointRounding.AwayFromZero);   // as Postgres round()
    /// <summary>The last moment a request is accepted: 7 days after the term starts.</summary>
    public static DateTime LastDayToAsk(DateTime starts) => starts.AddDays(7);
    public static bool WeekPassed(DateTime starts, DateTime at) => at > LastDayToAsk(starts);
}

/// <summary>Why a member asks for a refund on a term they have used. Staff review these; none of them is automatic. Guidance, not legal advice.</summary>
public static class RefundReasons
{
    public sealed record Reason(string Key, string Member, string Staff);
    public static readonly IReadOnlyList<Reason> All =
    [
        new("service_not_provided", "The gym didn't provide the agreed service", "gym didn't provide the agreed service"),
        new("misled", "I was misled when I signed up", "member says they were misled"),
        new("contract_allows", "My contract allows cancellation or a refund", "member says the contract allows it"),
        new("illness_or_relocation", "Illness or moving away", "illness or relocation"),
        new("change_of_mind", "I changed my mind", "change of mind, not automatic"),
    ];
    /// <summary>The key for what the member chose, or null if they backed out.</summary>
    public static string? KeyFor(string? memberText) => All.FirstOrDefault(r => r.Member == memberText)?.Key;
    public static string StaffText(string? key) => All.FirstOrDefault(r => r.Key == key)?.Staff ?? "no reason given";
}

public sealed record TermCard(MembershipTerm Term, string State, bool Activated, RefundRequest? Refund, bool CanRequestRefund,
    decimal RefundEstimate = 0, DateTime? LastDayToAsk = null, bool PastWindow = false)
{
    public string Dates => $"{Term.StartsAt.AddHours(8):d MMM yyyy, h:mm tt} – {Term.EndsAt.AddHours(8):d MMM yyyy, h:mm tt} (Manila)";
    public string? CoachAccessText => Term.CoachAccessAt is not null && State == Status.Active
        ? $"Coach access until {Term.EndsAt.AddHours(8):d MMM yyyy}"
        : null;
    /// <summary>Phone rows: the days only (Manila), "13 Oct – 12 Nov 2026"; the full times stay on Windows and in the refund dialog.</summary>
    public string ShortDates => $"{Term.StartsAt.AddHours(8):d MMM} – {Term.EndsAt.AddHours(8):d MMM yyyy}";
    /// <summary>Phone rows: a line only when a refund request or a cancellation exists; the chip and the refund dialog say the rest.</summary>
    public string? RefundNote => Refund is { } r ? RefundCard.StateText(r) : Term.CancelledAt is not null ? "This term is cancelled."
        : PastWindow ? PastWindowText : null;
    const string PastWindowText = "Refunds are only for a term's first 7 days, and this one is past that.";
    string AskBy => $"{LastDayToAsk?.AddHours(8):d MMM, h:mm tt}";   // Manila
    public string RefundText => Refund is { } r ? RefundCard.StateText(r) : Term.CancelledAt is not null ? "This term is cancelled."
        : CanRequestRefund ? (Activated ? $"You've checked in during this term, so staff review a refund. Ask by {AskBy}; it covers the days left when they approve."
                                       : $"Cancel by {AskBy} for about ₱{RefundEstimate:N2}, the days left when staff approve.")
        : PastWindow ? PastWindowText
        : State == Status.Expired ? "This term has ended." : "No verified payment available for a refund.";
}
public sealed record RefundCard(RefundRequest Request, string Member, string PackageName, string Dates, int LaterTerms = 0, string Period = "")
{
    /// <summary>The table's Status column: what is left to do, in two words (State has the full sentence for the side panel).</summary>
    public string Status => Request.Status switch
    {
        "pending" when Request.IsBooking && !Request.IsWalkInRequest => "Front desk",
        "pending" => "To review",
        "approved" => "To repay",
        "rejected" => "Rejected",
        "paid" => "Repaid",
        _ => Request.Status
    };
    public string ActionText => CanDecide ? "Review" : CanRepay ? "Repay" : "View";
    public bool NeedsAction => CanDecide || CanRepay;
    /// <summary>A term the member has used is only asked for until staff decide, so it must not read as a refund already owed.</summary>
    /// <summary>G22: a membership refund is worked out when staff approve, so until then it is an estimate.</summary>
    public string AmountLabel => !Request.IsBooking && Request.Status == "pending" ? "Estimated refund" : "Refund";
    public string ReviewText => Request.Kind switch
    {
        "cancel" => $"{Request.DecisionReason?.TrimEnd('.') ?? "Booking cancelled: down payment refunded"}.",   // G13 (0064): 24 hours or more before, the gym, or slot taken
        "no_show" => Request.Status == "pending" ? "The member says they didn't use the booking." : $"Didn't use it: {(Request.Status == "rejected" ? "declined" : "approved")} by the front desk.",
        "online" or "coach_access" => $"{Request.DecisionReason}.",   // 0095, 0150
        "day_pass" => "The front desk asked for this on the day the Day pass was paid for.",   // 0096
        "walk_in_booking" => Request.Status == "pending" ? "The front desk asked for this. Approving cancels the booking." : "The front desk asked for this.",
        _ => MembershipReview
    };
    /// <summary>0096: what approving or rejecting a walk-in's refund does; null for a membership term, which has its own wording.</summary>
    public string? DecisionNote(bool approve) => !Request.IsWalkInRequest ? null : !approve ? "Nothing changes for the customer."
        : (Request.Kind == "day_pass" ? "The check-in stays on record." : "The booking is cancelled and its slot is freed.") + " The refund will await manual repayment — no money is sent by approving.";
    /// <summary>0095, 0096: a refund tied only to a payment: online money that could not be applied, or a Day pass.</summary>
    public static RefundCard ForOnline(RefundRequest r, Payment? p, string? memberName) =>
        new(r, (p?.WalkInName is { } name && name != "Day pass" ? name : memberName) ?? (r.Kind == "day_pass" ? "No name given" : "Member"), r.Kind switch { "day_pass" => "Day pass", "coach_access" => "Coach access", _ => "Online payment" },   // 0150: coach access
            p is null ? "" : $"{p.PaymentCode} · {p.OnlineChannel ?? PayMethod.Name(p.Method)}", Period: $"{r.RequestedAt.ToUniversalTime().AddHours(8):d MMM, h:mm tt}");
    string MembershipReview => Request.WasActivated switch
    {
        false => "No check-in during this term: the days left are owed.",   // G22
        true => $"Checked in during this term, needs review: {RefundReasons.StaffText(Request.ReviewReason)}.",
        null => "",
    };
    /// <summary>D10: a later paid term has its own payment and stays, so staff should know before approving.</summary>
    public string? LaterTermsWarning => !CanDecide || LaterTerms == 0 ? null : LaterTerms == 1
        ? "This member also has 1 later paid term. It stays and is not refunded."
        : $"This member also has {LaterTerms} later paid terms. They stay and are not refunded.";
    public bool CanDecide => Request.Status == "pending" && (!Request.IsBooking || Request.IsWalkInRequest);   // F13: the front desk decides booking claims on Payments to verify
    public bool CanRepay => Request.Status == "approved";
    public string State => StateText(Request);
    public static string StateText(RefundRequest r) => r.Status switch
    {
        "pending" when r.IsWalkInRequest => "Waiting for your decision",
        "rejected" when r.IsWalkInRequest => "Rejected: " + r.DecisionReason,
        "pending" when r.IsBooking => "Waiting for the front desk to check",
        "approved" when r.IsBooking => "Awaiting manual repayment: " + r.DecisionReason,
        "rejected" when r.IsBooking => "Declined by the front desk: " + r.DecisionReason,
        "pending" => "Pending admin review — access unchanged",
        "approved" => "Cancelled — awaiting manual repayment",
        "rejected" => "Rejected — term unchanged: " + r.DecisionReason,
        "paid" => $"Repaid {(r.PaidAt is { } at ? ManilaTime.Of(at) : null):d MMM yyyy, h:mm tt} (Manila) · {PayMethod.Name(r.RepaymentMethod ?? "")}{(string.IsNullOrEmpty(r.RepaymentReference) ? "" : $" · {r.RepaymentReference}")}",
        _ => r.Status
    };
}

public interface ISubscriptionService
{
    Task<AppResult<IReadOnlyList<TermCard>>> TermsAsync();
    Task<AppResult<Payment>> ExtendAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr);
    Task<AppResult<IReadOnlyList<RefundCard>>> RefundsAsync();
    Task<AppResult<bool>> RequestRefundAsync(int termId, string reason, string? category = null);
    Task<AppResult<bool>> CancelPendingPaymentAsync(int paymentId);
    Task<AppResult<bool>> DecideAsync(int requestId, bool approve, string reason);
    /// <summary>0096: the desk asks for a walk-in's refund: a Day pass the same day, a booking 24 hours or more ahead, or later if the gym is cancelling it.</summary>
    Task<AppResult<bool>> RequestWalkInRefundAsync(int paymentId, string reason, bool gymCancels = false);
    Task<AppResult<bool>> DecideWalkInAsync(int requestId, bool approve, string reason);
    Task<AppResult<bool>> MarkPaidAsync(int requestId, string method, string? reference);
}

public sealed class SubscriptionService(ISupabaseGateway gw, TimeProvider? time = null) : ISubscriptionService
{
    readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>The real backend gives timestamps in local time (Manila), the demo in UTC; every term rule and the
    /// "+8 hours" dates on screen work in UTC (reported 2026-11-15: a term bought today showed "scheduled").</summary>
    public static MembershipTerm InUtc(MembershipTerm t)
    {
        t.StartsAt = t.StartsAt.ToUniversalTime(); t.EndsAt = t.EndsAt.ToUniversalTime(); t.CancelledAt = t.CancelledAt?.ToUniversalTime();
        return t;
    }
    public static string StateOf(MembershipTerm t, DateTime nowUtc) =>
        t.CancelledAt is not null ? Status.Cancelled : t.EndsAt <= nowUtc ? Status.Expired : t.StartsAt > nowUtc ? Status.Scheduled : Status.Active;
    public Task<AppResult<IReadOnlyList<TermCard>>> TermsAsync() => Safe.RunAsync<IReadOnlyList<TermCard>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var termsTask = gw.ListAsync<MembershipTerm>(t => t.MemberId == uid);
        var payTask = gw.ListAsync<Payment>(p => p.MemberId == uid);
        var refTask = gw.ListAsync<RefundRequest>(r => r.MemberId == uid);
        var ciTask = gw.ListAsync<GymCheckIn>(c => c.MemberId == uid);
        await Task.WhenAll(termsTask, payTask, refTask, ciTask);
        var terms = termsTask.Result;
        var payments = payTask.Result;
        var refunds = refTask.Result;
        var checkIns = ciTask.Result;
        var now = clock.GetUtcNow().UtcDateTime;
        return terms.Select(InUtc).OrderBy(t => t.CancelledAt is not null || t.EndsAt <= now).ThenBy(t => t.StartsAt).Select(t =>
        {
            var p = payments.FirstOrDefault(p => p.Id == t.PaymentId);
            var refund = refunds.FirstOrDefault(r => r.TermId == t.Id);
            var state = StateOf(t, now);
            var canAsk = t.CancelledAt is null && p is { Status: Status.Verified, Amount: > 0 } && now < t.EndsAt && refund is null && !RefundMath.WeekPassed(t.StartsAt, now);
            return new TermCard(t, state, checkIns.Any(c => c.CheckedInAt.ToUniversalTime() >= t.StartsAt && c.CheckedInAt.ToUniversalTime() < t.EndsAt), refund, canAsk,
                canAsk ? RefundMath.Due(p!.Amount, t.DurationDays, t.StartsAt, now) : 0, RefundMath.LastDayToAsk(t.StartsAt),
                t.CancelledAt is null && refund is null && now < t.EndsAt && RefundMath.WeekPassed(t.StartsAt, now));
        }).ToList();
    });

    public Task<AppResult<Payment>> ExtendAsync(int packageId, string? discountCode = null, string method = PayMethod.Qr) => Safe.RunAsync(() =>
    {
        Dictionary<string, object> args = new() { ["p_package_id"] = packageId };
        if (!string.IsNullOrWhiteSpace(discountCode)) args["p_discount_code"] = discountCode.Trim();
        if (method != PayMethod.Qr) args["p_method"] = method;   // 0084; online is the server's default
        return gw.RpcAsync<Payment>("extend_membership",args);
    });

    // "17 Oct – 16 Nov 2026"; the year shows twice only when the term crosses one
    static string ShortPeriod(DateTime from, DateTime to) => from.Year == to.Year ? $"{from:d MMM} – {to:d MMM yyyy}" : $"{from:d MMM yyyy} – {to:d MMM yyyy}";

    public Task<AppResult<IReadOnlyList<RefundCard>>> RefundsAsync() => Safe.RunAsync<IReadOnlyList<RefundCard>>(async () =>
    {
        var requests = await gw.ListAsync<RefundRequest>();
        if (requests.Count == 0) return [];
        var termsTask = gw.ListAsync<MembershipTerm>();
        var namesTask = Directory_.UserNamesAsync(gw);
        var bookingsTask = requests.Any(r => r.IsBooking) ? gw.ListAsync<Reservation>() : Task.FromResult<IReadOnlyList<Reservation>>([]);
        var paymentsTask = requests.Any(r => r.Kind is "online" or "day_pass" or "coach_access") ? gw.ListAsync<Payment>() : Task.FromResult<IReadOnlyList<Payment>>([]);
        await Task.WhenAll(termsTask, namesTask, bookingsTask, paymentsTask);
        var terms = termsTask.Result.Select(InUtc).ToList();
        var names = namesTask.Result;
        var bookings = bookingsTask.Result.ToDictionary(r => r.Id);
        var amenities = bookings.Count > 0 ? (await gw.ListAsync<Amenity>()).ToDictionary(a => a.Id, a => a.Name) : [];
        var payments = paymentsTask.Result.ToDictionary(p => p.Id);
        return requests.OrderBy(r => r.Status is "paid" or "rejected").ThenBy(r => r.RequestedAt).Select(r =>
        {
            if (r.Kind is "online" or "day_pass" or "coach_access") return RefundCard.ForOnline(r, payments.GetValueOrDefault(r.PaymentId), names.GetValueOrDefault(r.MemberId ?? ""));
            if (r.IsBooking && bookings.GetValueOrDefault(r.ReservationId ?? 0) is { } b)   // F13: a booking refund names the amenity and the booked time
                return new RefundCard(r, b.WalkInName ?? names.GetValueOrDefault(r.MemberId ?? "", "Member"), $"{amenities.GetValueOrDefault(b.AmenityId, "Amenity")} booking",
                    $"{b.StartsAt.ToUniversalTime().AddHours(8):d MMM yyyy, h:mm tt} – {b.EndsAt.ToUniversalTime().AddHours(8):h:mm tt} (Manila)",
                    Period: $"{b.StartsAt.ToUniversalTime().AddHours(8):d MMM, h:mm tt}");
            var term = terms.FirstOrDefault(t => t.Id == r.TermId);
            var dates = term is null ? "" : $"{term.StartsAt.AddHours(8):d MMM yyyy, h:mm tt} – {term.EndsAt.AddHours(8):d MMM yyyy, h:mm tt} (Manila)";
            var later = term is null ? 0 : terms.Count(t => t.MemberId == r.MemberId && t.CancelledAt is null && t.StartsAt >= term.EndsAt);
            return new RefundCard(r,names.GetValueOrDefault(r.MemberId,"Member"),term?.PackageName ?? "Membership",dates,later,term is null ? "" : ShortPeriod(term.StartsAt.AddHours(8),term.EndsAt.AddHours(8)));
        }).ToList();
    });

    public Task<AppResult<bool>> RequestRefundAsync(int termId, string reason, string? category = null)
    {
        Dictionary<string,object> args=new() { ["p_term_id"]=termId,["p_reason"]=reason.Trim() };
        if (category is not null) args["p_category"]=category;
        return Change("request_membership_refund",args);
    }
    public Task<AppResult<bool>> CancelPendingPaymentAsync(int paymentId) => Change("cancel_pending_payment",new() { ["p_payment_id"]=paymentId });
    public Task<AppResult<bool>> DecideAsync(int requestId, bool approve, string reason) => Change("decide_membership_refund",new() { ["p_request_id"]=requestId,["p_approve"]=approve,["p_reason"]=reason.Trim() });
    public Task<AppResult<bool>> RequestWalkInRefundAsync(int paymentId, string reason, bool gymCancels = false) =>
        Change("request_walk_in_refund", new() { ["p_payment_id"] = paymentId, ["p_reason"] = reason.Trim(), ["p_gym_cancels"] = gymCancels });
    public Task<AppResult<bool>> DecideWalkInAsync(int requestId, bool approve, string reason) =>
        Change("decide_walk_in_refund", new() { ["p_request_id"] = requestId, ["p_approve"] = approve, ["p_reason"] = reason.Trim() });
    public Task<AppResult<bool>> MarkPaidAsync(int requestId, string method, string? reference)
    {
        Dictionary<string,object> args=new() { ["p_request_id"]=requestId,["p_method"]=method };
        if (!string.IsNullOrWhiteSpace(reference)) args["p_reference"]=reference.Trim();
        return Change("record_membership_refund_paid",args);
    }
    Task<AppResult<bool>> Change(string function, Dictionary<string, object> args) => Safe.RunAsync(async () => { await gw.RpcAsync(function,args); return true; });
}
