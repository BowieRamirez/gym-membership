using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

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

public sealed record TermCard(MembershipTerm Term, string State, bool Activated, RefundRequest? Refund, bool CanRequestRefund)
{
    public string Dates => $"{Term.StartsAt.AddHours(8):d MMM yyyy, h:mm tt} – {Term.EndsAt.AddHours(8):d MMM yyyy, h:mm tt} (Manila)";
    /// <summary>Phone rows: the days only (Manila), "13 Oct – 12 Nov 2026"; the full times stay on Windows and in the refund dialog.</summary>
    public string ShortDates => $"{Term.StartsAt.AddHours(8):d MMM} – {Term.EndsAt.AddHours(8):d MMM yyyy}";
    /// <summary>Phone rows: a line only when a refund request or a cancellation exists; the chip and the refund dialog say the rest.</summary>
    public string? RefundNote => Refund is { } r ? RefundCard.StateText(r) : Term.CancelledAt is not null ? "This term is cancelled." : null;
    public string RefundText => Refund is { } r ? RefundCard.StateText(r) : Term.CancelledAt is not null ? "This term is cancelled."
        : CanRequestRefund ? (Activated ? "You have checked in during this term. You can ask for a refund, and staff review each request." : "You haven't used this term yet. You can cancel it for a full refund.")
        : State == Status.Expired ? "This term has ended." : "No verified payment available for a refund.";
}
public sealed record RefundCard(RefundRequest Request, string Member, string PackageName, string Dates, int LaterTerms = 0)
{
    /// <summary>A term the member has used is only asked for until staff decide, so it must not read as a refund already owed.</summary>
    public string AmountLabel => Request.WasActivated == true ? "Refund requested" : "Full refund";
    public string ReviewText => Request.Kind switch
    {
        "cancel" => "Booking cancelled 1 hour or more before the start: full refund owed.",
        "no_show" => Request.Status == "pending" ? "The member says they didn't use the booking." : $"Didn't use it: {(Request.Status == "rejected" ? "declined" : "approved")} by the front desk.",
        _ => MembershipReview
    };
    string MembershipReview => Request.WasActivated switch
    {
        false => "No check-in during this term: full refund owed.",
        true => $"Checked in during this term, needs review: {RefundReasons.StaffText(Request.ReviewReason)}.",
        null => "",
    };
    /// <summary>D10: a later paid term has its own payment and stays, so staff should know before approving.</summary>
    public string? LaterTermsWarning => !CanDecide || LaterTerms == 0 ? null : LaterTerms == 1
        ? "This member also has 1 later paid term. It stays and is not refunded."
        : $"This member also has {LaterTerms} later paid terms. They stay and are not refunded.";
    public bool CanDecide => Request.Status == "pending" && !Request.IsBooking;   // F13: the front desk decides booking claims on Payments to verify
    public bool CanRepay => Request.Status == "approved";
    public string State => StateText(Request);
    public static string StateText(RefundRequest r) => r.Status switch
    {
        "pending" when r.IsBooking => "Waiting for the front desk to check",
        "approved" when r.IsBooking => "Awaiting manual repayment: " + r.DecisionReason,
        "rejected" when r.IsBooking => "Declined by the front desk: " + r.DecisionReason,
        "pending" => "Pending admin review — access unchanged",
        "approved" => "Cancelled — awaiting manual repayment",
        "rejected" => "Rejected — term unchanged: " + r.DecisionReason,
        "paid" => $"Repaid {r.PaidAt?.AddHours(8):d MMM yyyy, h:mm tt} (Manila) · {r.RepaymentMethod} · {r.RepaymentReference}",
        _ => r.Status
    };
}

public interface ISubscriptionService
{
    Task<AppResult<IReadOnlyList<TermCard>>> TermsAsync();
    Task<AppResult<Payment>> ExtendAsync(int packageId, string? discountCode = null);
    Task<AppResult<IReadOnlyList<RefundCard>>> RefundsAsync();
    Task<AppResult<bool>> RequestRefundAsync(int termId, string reason, string? category = null);
    Task<AppResult<bool>> CancelPendingPaymentAsync(int paymentId);
    Task<AppResult<bool>> DecideAsync(int requestId, bool approve, string reason);
    Task<AppResult<bool>> MarkPaidAsync(int requestId, string method, string? reference);
}

public sealed class SubscriptionService(ISupabaseGateway gw, TimeProvider? time = null) : ISubscriptionService
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    public Task<AppResult<IReadOnlyList<TermCard>>> TermsAsync() => Safe.RunAsync<IReadOnlyList<TermCard>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var terms = await gw.ListAsync<MembershipTerm>(t => t.MemberId == uid);
        var payments = await gw.ListAsync<Payment>(p => p.MemberId == uid);
        var refunds = await gw.ListAsync<RefundRequest>(r => r.MemberId == uid);
        var checkIns = await gw.ListAsync<GymCheckIn>(c => c.MemberId == uid);
        var now = clock.GetUtcNow().UtcDateTime;
        return terms.OrderBy(t => t.CancelledAt is not null || t.EndsAt <= now).ThenBy(t => t.StartsAt).Select(t =>
        {
            var p = payments.FirstOrDefault(p => p.Id == t.PaymentId);
            var refund = refunds.FirstOrDefault(r => r.TermId == t.Id);
            var state = t.CancelledAt is not null ? Status.Cancelled : t.EndsAt <= now ? Status.Expired : t.StartsAt > now ? Status.Scheduled : Status.Active;
            return new TermCard(t, state, checkIns.Any(c => c.CheckedInAt >= t.StartsAt && c.CheckedInAt < t.EndsAt), refund, t.CancelledAt is null && p is { Status: Status.Verified, Amount: > 0 } && now < t.EndsAt && refund is null);
        }).ToList();
    });

    public Task<AppResult<Payment>> ExtendAsync(int packageId, string? discountCode = null) => Safe.RunAsync(() =>
    {
        Dictionary<string, object> args = new() { ["p_package_id"] = packageId };
        if (!string.IsNullOrWhiteSpace(discountCode)) args["p_discount_code"] = discountCode.Trim();
        return gw.RpcAsync<Payment>("extend_membership",args);
    });

    public Task<AppResult<IReadOnlyList<RefundCard>>> RefundsAsync() => Safe.RunAsync<IReadOnlyList<RefundCard>>(async () =>
    {
        var requests = await gw.ListAsync<RefundRequest>();
        var terms = await gw.ListAsync<MembershipTerm>();
        var names = await Directory_.UserNamesAsync(gw);
        var bookings = requests.Any(r => r.IsBooking) ? (await gw.ListAsync<Reservation>()).ToDictionary(r => r.Id) : [];
        var amenities = bookings.Count > 0 ? (await gw.ListAsync<Amenity>()).ToDictionary(a => a.Id, a => a.Name) : [];
        return requests.OrderBy(r => r.Status is "paid" or "rejected").ThenBy(r => r.RequestedAt).Select(r =>
        {
            if (r.IsBooking && bookings.GetValueOrDefault(r.ReservationId ?? 0) is { } b)   // F13: a booking refund names the amenity and the booked time
                return new RefundCard(r, names.GetValueOrDefault(r.MemberId, "Member"), $"{amenities.GetValueOrDefault(b.AmenityId, "Amenity")} booking",
                    $"{b.StartsAt.ToUniversalTime().AddHours(8):d MMM yyyy, h:mm tt} – {b.EndsAt.ToUniversalTime().AddHours(8):h:mm tt} (Manila)");
            var term = terms.FirstOrDefault(t => t.Id == r.TermId);
            var dates = term is null ? "" : $"{term.StartsAt.AddHours(8):d MMM yyyy, h:mm tt} – {term.EndsAt.AddHours(8):d MMM yyyy, h:mm tt} (Manila)";
            var later = term is null ? 0 : terms.Count(t => t.MemberId == r.MemberId && t.CancelledAt is null && t.StartsAt >= term.EndsAt);
            return new RefundCard(r,names.GetValueOrDefault(r.MemberId,"Member"),term?.PackageName ?? "Membership",dates,later);
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
    public Task<AppResult<bool>> MarkPaidAsync(int requestId, string method, string? reference)
    {
        Dictionary<string,object> args=new() { ["p_request_id"]=requestId,["p_method"]=method };
        if (!string.IsNullOrWhiteSpace(reference)) args["p_reference"]=reference.Trim();
        return Change("record_membership_refund_paid",args);
    }
    Task<AppResult<bool>> Change(string function, Dictionary<string, object> args) => Safe.RunAsync(async () => { await gw.RpcAsync(function,args); return true; });
}
