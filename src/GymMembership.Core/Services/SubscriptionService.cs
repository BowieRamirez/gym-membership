using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public sealed record TermCard(MembershipTerm Term, string State, DateTime? RefundDeadline, RefundRequest? Refund, bool CanRequestRefund)
{
    public string Dates => $"{Term.StartsAt.AddHours(8):dd MMM yyyy, HH:mm} – {Term.EndsAt.AddHours(8):dd MMM yyyy, HH:mm} (Manila)";
    public string RefundText => Refund is { } r ? RefundCard.StateText(r) : Term.CancelledAt is not null
        ? "This term is cancelled."
        : RefundDeadline is { } d
            ? $"Cancellation requests {(CanRequestRefund ? "close" : "closed")} {d.AddHours(8):dd MMM yyyy, HH:mm} (Manila). The server checks the exact deadline."
            : "Legacy term: no verified payment available for a refund.";
}
public sealed record RefundCard(RefundRequest Request, string Member, string PackageName, string Dates)
{
    public bool CanDecide => Request.Status == "pending";
    public bool CanRepay => Request.Status == "approved";
    public string State => StateText(Request);
    public static string StateText(RefundRequest r) => r.Status switch
    {
        "pending" => "Pending admin review — access unchanged",
        "approved" => "Cancelled — awaiting manual repayment",
        "rejected" => "Rejected — term unchanged: " + r.DecisionReason,
        "paid" => $"Repaid {r.PaidAt?.AddHours(8):dd MMM yyyy, HH:mm} (Manila) · {r.RepaymentMethod} · {r.RepaymentReference}",
        _ => r.Status
    };
}

public interface ISubscriptionService
{
    Task<AppResult<IReadOnlyList<TermCard>>> TermsAsync();
    Task<AppResult<Payment>> ExtendAsync(int packageId, string? discountCode = null);
    Task<AppResult<IReadOnlyList<RefundCard>>> RefundsAsync();
    Task<AppResult<bool>> RequestRefundAsync(int termId, string reason);
    Task<AppResult<bool>> CancelPendingPaymentAsync(int paymentId);
    Task<AppResult<bool>> DecideAsync(int requestId, bool approve, string reason);
    Task<AppResult<bool>> MarkPaidAsync(int requestId, string method, string? reference);
}

public sealed class SubscriptionService(ISupabaseGateway gw) : ISubscriptionService
{
    public Task<AppResult<IReadOnlyList<TermCard>>> TermsAsync() => Safe.RunAsync<IReadOnlyList<TermCard>>(async () =>
    {
        var uid = gw.CurrentUserId;
        var terms = await gw.ListAsync<MembershipTerm>(t => t.MemberId == uid);
        var payments = await gw.ListAsync<Payment>(p => p.MemberId == uid);
        var refunds = await gw.ListAsync<RefundRequest>(r => r.MemberId == uid);
        var now = DateTime.UtcNow;
        return terms.OrderBy(t => t.CancelledAt is not null || t.EndsAt <= now).ThenBy(t => t.StartsAt).Select(t =>
        {
            var p = payments.FirstOrDefault(p => p.Id == t.PaymentId);
            var refund = refunds.FirstOrDefault(r => r.TermId == t.Id);
            var deadline = p?.VerifiedAt?.AddHours(24);
            var state = t.CancelledAt is not null ? Status.Cancelled : t.EndsAt <= now ? Status.Expired : t.StartsAt > now ? Status.Scheduled : Status.Active;
            return new TermCard(t, state, deadline, refund, t.CancelledAt is null && p?.Status == Status.Verified && deadline > now && refund is null);
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
        return requests.OrderBy(r => r.Status is "paid" or "rejected").ThenBy(r => r.RequestedAt).Select(r =>
        {
            var term = terms.FirstOrDefault(t => t.Id == r.TermId);
            var dates = term is null ? "" : $"{term.StartsAt.AddHours(8):dd MMM yyyy, HH:mm} – {term.EndsAt.AddHours(8):dd MMM yyyy, HH:mm} (Manila)";
            return new RefundCard(r,names.GetValueOrDefault(r.MemberId,"Member"),term?.PackageName ?? "Membership",dates);
        }).ToList();
    });

    public Task<AppResult<bool>> RequestRefundAsync(int termId, string reason) => Change("request_membership_refund",new() { ["p_term_id"]=termId,["p_reason"]=reason.Trim() });
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
