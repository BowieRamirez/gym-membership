using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

public sealed partial class DemoGateway
{
    void SnapshotMembershipPayment(Payment pay, MembershipPackage? selected = null)
    {
        if (pay.Kind is not (PayKind.Membership or PayKind.Renewal)) return;
        var id = pay.UserMembershipPackageId ?? All<MembershipRenewal>().FirstOrDefault(r => r.Id == pay.RenewalId)?.UserMembershipPackageId;
        var packageId = All<UserMembershipPackage>().FirstOrDefault(u => u.Id == id)?.MembershipPackageId;
        var pkg = selected ?? All<MembershipPackage>().FirstOrDefault(p => p.Id == packageId);
        if (pkg is null) return;
        pay.PackageSnapshotId=pkg.Id; pay.PackageSnapshotName=pkg.Name; pay.PackageSnapshotDays=pkg.DurationDays;
    }

    void SeedMembershipTerms()
    {
        foreach (var pay in All<Payment>()) SnapshotMembershipPayment(pay);
        // Demo report history contains extra illustrative receipts, not extra entitlements.
        foreach (var u in All<UserMembershipPackage>().Where(u => u.Status is Status.Active or Status.Expired && u.StartsAt is not null && u.EndsAt is not null).ToList())
        {
            var pkg=All<MembershipPackage>().First(p=>p.Id==u.MembershipPackageId);
            var p=All<Payment>().Where(p=>p.UserMembershipPackageId==u.Id && p.Status==Status.Verified).OrderBy(p=>p.CreatedAt).FirstOrDefault();
            Add(new MembershipTerm {MemberId=u.MemberId,MembershipId=u.Id,PaymentId=p?.Id,PackageId=pkg.Id,PackageName=pkg.Name,
                DurationDays=pkg.DurationDays,AmountPaid=p?.Amount??0,StartsAt=u.StartsAt!.Value,EndsAt=u.EndsAt!.Value,SnapshotSource="legacy_catalog",CreatedAt=SubscriptionNow});
        }
    }

    void ProjectTerms(string member)
    {
        var now=SubscriptionNow;
        foreach(var u in All<UserMembershipPackage>().Where(u=>u.MemberId==member))
        {
            var all=All<MembershipTerm>().Where(t=>t.MembershipId==u.Id).ToList();
            if(all.Count==0) continue;
            var valid=all.Where(t=>t.CancelledAt is null).ToList();
            if(valid.Count==0) {u.Status=Status.Cancelled; continue;}
            u.StartsAt=valid.Min(t=>t.StartsAt); u.EndsAt=valid.Max(t=>t.EndsAt);
            u.Status=valid.Any(t=>t.StartsAt<=now && t.EndsAt>now)?Status.Active:Status.Expired;
        }
    }

    void ActivateMembershipTerm(Payment pay)
    {
        if(All<MembershipTerm>().Any(t=>t.PaymentId==pay.Id)) throw Err("already_processed");
        var renewal=All<MembershipRenewal>().FirstOrDefault(r=>r.Id==pay.RenewalId);
        var umpId=pay.UserMembershipPackageId??renewal!.UserMembershipPackageId;
        var start=All<MembershipTerm>().Where(t=>t.MemberId==pay.MemberId && t.CancelledAt is null).Select(t=>t.EndsAt)
            .Append(pay.VerifiedAt!.Value).Max();
        var end=start.AddDays(pay.PackageSnapshotDays!.Value);
        Add(new MembershipTerm {MemberId=pay.MemberId!,MembershipId=umpId,PaymentId=pay.Id,PackageId=pay.PackageSnapshotId!.Value,
            PackageName=pay.PackageSnapshotName!,DurationDays=pay.PackageSnapshotDays.Value,AmountPaid=pay.Amount,StartsAt=start,EndsAt=end,CreatedAt=SubscriptionNow});
        if(renewal is not null) {renewal.Status=Status.Completed;renewal.CompletedAt=pay.VerifiedAt;renewal.PreviousEndsAt=start;renewal.NewStartsAt=start;renewal.NewEndsAt=end;}
        ProjectTerms(pay.MemberId!);
    }

    Payment CreateExtension(string member,int packageId,Dictionary<string,object> args)
    {
        RequireActive();
        if(!All<Member>().Any(m=>m.UserId==member) || !All<Profile>().Any(p=>p.Id==member && p.IsActive)) throw Err("not_found");
        if(All<Payment>().Any(p=>p.MemberId==member && p.Status==Status.Pending && p.Kind is PayKind.Membership or PayKind.Renewal)) throw Err("renewal_pending");
        var pkg=All<MembershipPackage>().FirstOrDefault(p=>p.Id==packageId && p.IsActive)??throw Err("not_found");
        var discount=Discounted(member,args,pkg.Price);
        var ump=All<UserMembershipPackage>().Where(u=>u.MemberId==member && u.StartsAt is not null).OrderByDescending(u=>u.Id).FirstOrDefault();
        Payment pay;
        if(ump is null)
        {
            ump=Add(new UserMembershipPackage {MemberId=member,MembershipPackageId=pkg.Id,Status=Status.Pending});
            pay=NewQrPayment(PayKind.Membership,member,discount,ump:ump.Id);
        }
        else
        {
            var start=All<MembershipTerm>().Where(t=>t.MemberId==member && t.CancelledAt is null).Select(t=>t.EndsAt).Append(SubscriptionNow).Max();
            var ren=Add(new MembershipRenewal {UserMembershipPackageId=ump.Id,PreviousEndsAt=start,NewStartsAt=start,NewEndsAt=start.AddDays(pkg.DurationDays)});
            pay=NewQrPayment(PayKind.Renewal,member,discount,renewal:ren.Id);
        }
        SnapshotMembershipPayment(pay,pkg);
        return pay;
    }

    void RequireRefundAdmin() {RequireActive();if(!RolesOf(Uid).Contains("admin")) throw Err("forbidden");}

    bool SubscriptionCall(string fn,Dictionary<string,object> a,out object? result)
    {
        result=null; var now=SubscriptionNow;
        int I(string k)=>Convert.ToInt32(a[k]);
        string? Text(string k)=>a.TryGetValue(k,out var v)?Convert.ToString(v)?.Trim():null;
        string Reason() {var reason=Text("p_reason");if(string.IsNullOrWhiteSpace(reason)||reason.Length>1000) throw Err("bad_input");return reason;}
        switch(fn)
        {
            case "is_active_member": RequireActive();result=ActiveMember(Text("p_user")??Uid);return true;
            case "extend_membership": result=Clone(CreateExtension(Uid,I("p_package_id"),a));return true;
            case "cancel_pending_payment":
            {
                RequireActive();
                var p=All<Payment>().FirstOrDefault(p=>p.Id==I("p_payment_id") && p.MemberId==Uid && p.Kind is PayKind.Membership or PayKind.Renewal)??throw Err("not_found");
                if(p.Status!=Status.Pending) throw Err("already_processed");
                p.Status=Status.Rejected;p.RejectedReason="Cancelled by member";CancelTarget(p);
                Audit("payment.cancel","payments",p.Id.ToString());return true;
            }
            case "request_membership_refund":
            {
                RequireActive(); var reason=Reason();
                var term=All<MembershipTerm>().FirstOrDefault(t=>t.Id==I("p_term_id")&&t.MemberId==Uid)??throw Err("not_found");
                var p=All<Payment>().FirstOrDefault(p=>p.Id==term.PaymentId);
                if(p?.Status!=Status.Verified||p.Amount<=0||term.CancelledAt is not null) throw Err("not_refundable");
                if(All<RefundRequest>().Any(r=>r.TermId==term.Id)) throw Err("already_requested");
                if(p.VerifiedAt is null || now>=p.VerifiedAt.Value.AddHours(24)) throw Err("refund_window_closed");
                var r=Add(new RefundRequest {TermId=term.Id,PaymentId=p.Id,MemberId=Uid,Amount=p.Amount,Reason=reason,RequestedAt=now});
                Audit("refund.request","refund_requests",r.Id.ToString());result=Clone(r);return true;
            }
            case "decide_membership_refund":
            {
                RequireRefundAdmin();var reason=Reason();var approve=Convert.ToBoolean(a["p_approve"]);
                var r=All<RefundRequest>().FirstOrDefault(r=>r.Id==I("p_request_id"))??throw Err("not_found");
                if(r.MemberId==Uid) throw Err("self_decision");
                if(r.Status!="pending") throw Err("already_processed");
                var p=All<Payment>().First(p=>p.Id==r.PaymentId);
                if(p.Status!=Status.Verified||r.Amount!=p.Amount) throw Err("not_refundable");
                var t=All<MembershipTerm>().First(t=>t.Id==r.TermId);
                if(approve && t.CancelledAt is not null) throw Err("already_processed");
                if(approve) {t.CancelledAt=now;t.CancelledBy=Uid;t.CancellationReason="Refund approved";ProjectTerms(r.MemberId);}
                r.Status=approve?"approved":"rejected";r.DecidedBy=Uid;r.DecidedAt=now;r.DecisionReason=reason;
                Notify(r.MemberId,"payment",approve?"Cancellation approved; refund awaiting repayment":"Cancellation request rejected",reason);
                Audit("refund.decide","refund_requests",r.Id.ToString());return true;
            }
            case "record_membership_refund_paid":
            {
                RequireRefundAdmin();var method=Text("p_method");var reference=Text("p_reference");
                if(method is not (PayMethod.Cash or PayMethod.Gcash or PayMethod.BankTransfer) || method!=PayMethod.Cash&&string.IsNullOrWhiteSpace(reference)) throw Err("bad_input");
                var r=All<RefundRequest>().FirstOrDefault(r=>r.Id==I("p_request_id"))??throw Err("not_found");
                if(r.MemberId==Uid) throw Err("self_decision");
                if(r.Status!="approved") throw Err("already_processed");
                var p=All<Payment>().First(p=>p.Id==r.PaymentId);
                if(p.Status!=Status.Verified||r.Amount!=p.Amount) throw Err("not_refundable");
                r.Status="paid";r.PaidAt=now;r.PaidBy=Uid;r.RepaymentMethod=method;r.RepaymentReference=reference;
                Notify(r.MemberId,"payment","Refund repaid",method);Audit("refund.paid","refund_requests",r.Id.ToString());return true;
            }
            case "void_payment":
            {
                Require("payments:void");var reason=Reason();
                var p=All<Payment>().FirstOrDefault(p=>p.Id==I("p_payment_id"))??throw Err("not_found");
                if(All<RefundRequest>().Any(r=>r.PaymentId==p.Id && r.Status is "pending" or "approved" or "paid")) throw Err("refund_in_progress");
                if(p.Kind is not (PayKind.Membership or PayKind.Renewal)) return false;
                if(p.Status!=Status.Verified) throw Err("already_processed");
                p.Status=Status.Voided;p.VoidedBy=Uid;p.VoidedAt=now;p.VoidReason=reason;
                foreach(var t in All<MembershipTerm>().Where(t=>t.PaymentId==p.Id)) {t.CancelledAt=now;t.CancelledBy=Uid;t.CancellationReason=reason;}
                foreach(var r in All<MembershipRenewal>().Where(r=>r.Id==p.RenewalId)) r.Status=Status.Cancelled;
                ProjectTerms(p.MemberId!);Notify(p.MemberId,"payment","Payment voided",reason);Audit("payment.void","payments",p.Id.ToString());return true;
            }
            case "revenue_summary":
            case "daily_cash_summary":
            {
                var cash=fn=="daily_cash_summary";Require(cash?"reports:cash":"reports:revenue");
                var from=DateTime.Parse(Text(cash?"p_day":"p_from")!).Date;
                var to=cash?from:DateTime.Parse(Text("p_to")!).Date;
                var amounts=All<Payment>().Where(p=>p.Status==Status.Verified && p.VerifiedAt is not null)
                    .Select(p=>(Day:p.VerifiedAt!.Value.AddHours(8).Date,Kind:p.Kind,Method:p.Method,Count:1L,Amount:p.Amount))
                    .Concat(All<RefundRequest>().Where(r=>cash ? r.Status=="paid" : r.Status is "approved" or "paid").Select(r=>
                        (Day:(cash ? r.PaidAt! : r.DecidedAt!).Value.AddHours(8).Date,Kind:All<Payment>().First(p=>p.Id==r.PaymentId).Kind,
                         Method:cash ? r.RepaymentMethod! : All<Payment>().First(p=>p.Id==r.PaymentId).Method,Count:0L,Amount:-r.Amount)))
                    .Where(r=>r.Day>=from&&r.Day<=to&&(!cash||r.Method==PayMethod.Cash)).ToList();
                result=cash ? amounts.GroupBy(r=>r.Kind).OrderBy(g=>g.Key).Select(g=>new CashRow(g.Key,g.Sum(r=>r.Count),g.Sum(r=>r.Amount))).ToList()
                    : (object)amounts.GroupBy(r=>(r.Day,r.Kind,r.Method)).OrderBy(g=>g.Key.Day).ThenBy(g=>g.Key.Kind).ThenBy(g=>g.Key.Method)
                        .Select(g=>new RevenueRow(g.Key.Day,g.Key.Kind,g.Key.Method,g.Sum(r=>r.Count),g.Sum(r=>r.Amount))).ToList();
                return true;
            }
            default:return false;
        }
    }
}
