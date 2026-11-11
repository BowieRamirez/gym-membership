using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class SubscriptionTests
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 17, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    internal static async Task<(DemoGateway Gw, Clock Clock, SubscriptionService Svc, MembershipPackage Monthly, MembershipPackage Annual)> Setup()
    {
        var clock = new Clock(); var gw = new DemoGateway(clock: clock);
        await gw.SignUpAsync("phase7@demo.io", "secret12", "Ana", "Reyes");
        var packages = await gw.ListAsync<MembershipPackage>();
        return (gw, clock, new SubscriptionService(gw, clock), packages.First(p => p.Name == "Monthly"), packages.First(p => p.Name == "Annual"));
    }
    internal static async Task Verify(DemoGateway gw, Payment pay)
    {
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, true)).Ok);
        await gw.SignInAsync("phase7@demo.io", "secret12");
    }

    static async Task CheckIn(DemoGateway gw)
    {
        var me = gw.CurrentUserId!;
        var pass = (await new AttendanceService(gw).MyPassAsync()).Value!.Code;   // F6: the desk scans a fresh pass, then checks in
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await new AttendanceService(gw).ScanAsync(pass);
        Assert.True((await new AttendanceService(gw).CheckInAsync(me)).Ok);
        await gw.SignInAsync("phase7@demo.io", "secret12");
    }

    [Fact]
    public async Task Paid_extension_keeps_current_term_and_queues_the_selected_package()
    {
        var (gw, _, svc, monthly, annual) = await Setup();
        var p = (await svc.ExtendAsync(monthly.Id)).Value!;
        Assert.Empty((await svc.TermsAsync()).Value!); await Verify(gw, p);
        var first = Assert.Single((await svc.TermsAsync()).Value!).Term;
        p = (await svc.ExtendAsync(annual.Id)).Value!; await Verify(gw,p);
        var terms = (await svc.TermsAsync()).Value!.OrderBy(t => t.Term.StartsAt).ToList();
        Assert.Equal(first.EndsAt,terms[0].Term.EndsAt); Assert.Equal(first.EndsAt,terms[1].Term.StartsAt);
        Assert.Equal(365,(terms[1].Term.EndsAt-terms[1].Term.StartsAt).TotalDays);
        Assert.Equal(("Annual",14000m,"scheduled"),(terms[1].Term.PackageName,terms[1].Term.AmountPaid,terms[1].State));
        Assert.True((await new MembershipService(gw).AccessAsync()).Value!.Active);
    }

    [Fact]
    public async Task Catalog_changes_do_not_change_purchase_snapshot()
    {
        var (gw, _, svc, monthly, annual) = await Setup();
        var p = (await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var extension = (await svc.ExtendAsync(annual.Id)).Value!;
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        annual.Name="Changed Annual"; annual.Price=18000; annual.DurationDays=366;
        await gw.UpdateAsync(annual);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(extension.Id,true)).Ok);
        await gw.SignInAsync("phase7@demo.io","secret12");
        var term = (await svc.TermsAsync()).Value!.Single(t=>t.Term.PaymentId==extension.Id);
        Assert.Equal(("Annual",14000m,365),(term.Term.PackageName,term.Term.AmountPaid,term.Term.DurationDays));
        Assert.Contains("Annual",(await new MembershipService(gw).MyPaymentsAsync()).Value!.First(p=>p.Id==extension.Id).What);
    }

    [Fact]
    public async Task Retrying_a_rejected_extension_keeps_the_selected_package_and_uses_its_new_price()
    {
        var (gw, _, svc, monthly, annual) = await Setup();
        var p = (await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var extension = (await svc.ExtendAsync(annual.Id)).Value!;
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(extension.Id,false,"Proof does not match")).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        annual.Price=18000; await gw.UpdateAsync(annual);
        await gw.SignInAsync("phase7@demo.io","secret12");
        var memberships = new MembershipService(gw);
        var item = (await memberships.MyPaymentsAsync()).Value!.Single(p=>p.Id==extension.Id);
        Assert.True(item.CanPayAgain);
        var retry = await memberships.PayAgainAsync(item);
        Assert.True(retry.Ok,retry.Message);
        Assert.Equal(annual.Id,retry.Value!.PackageSnapshotId);
        Assert.Equal(("Annual",18000m,365),(retry.Value.PackageSnapshotName,retry.Value.Amount,retry.Value.PackageSnapshotDays));
    }

    [Fact]
    public async Task Request_pending_preserves_access_approval_cancels_only_selected_term_and_repayment_is_separate()
    {
        var (gw, _, svc, monthly, annual) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        var extension=(await svc.ExtendAsync(annual.Id)).Value!; await Verify(gw,extension);
        var future=(await svc.TermsAsync()).Value!.Single(t=>t.Term.PaymentId==extension.Id).Term;
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans","change_of_mind")).Ok);
        Assert.True((await new MembershipService(gw).AccessAsync()).Value!.Active);
        Assert.False((await svc.RequestRefundAsync(term.Id,"Twice")).Ok);
        var request=Assert.Single((await svc.RefundsAsync()).Value!).Request;
        await gw.SignInAsync("employee@demo.io",DemoGateway.DemoPassword);
        Assert.Empty((await svc.RefundsAsync()).Value!);
        Assert.False((await svc.DecideAsync(request.Id,true,"Approve")).Ok);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        Assert.False((await svc.MarkPaidAsync(request.Id,PayMethod.Cash,null)).Ok);
        Assert.True((await svc.DecideAsync(request.Id,true,"Approve")).Ok);
        Assert.False((await svc.DecideAsync(request.Id,true,"Again")).Ok);
        var after=(await gw.ListAsync<MembershipTerm>(t=>t.Id==future.Id)).Single();
        Assert.Equal(future.StartsAt,after.StartsAt); Assert.Null(after.CancelledAt);
        Assert.Null((await gw.ListAsync<RefundRequest>()).Single().PaidAt);
        Assert.False((await svc.MarkPaidAsync(request.Id,PayMethod.Gcash,null)).Ok);
        Assert.True((await svc.MarkPaidAsync(request.Id,PayMethod.Gcash,"REFUND-ONE")).Ok);
        Assert.False((await svc.MarkPaidAsync(request.Id,PayMethod.Gcash,"REFUND-TWO")).Ok);
        await gw.SignInAsync("phase7@demo.io","secret12");
        Assert.False((await new MembershipService(gw).AccessAsync()).Value!.Active);
        Assert.Equal("paid",Assert.Single((await svc.RefundsAsync()).Value!).Request.Status);
    }

    [Fact]
    public async Task Admin_queue_refresh_waits_for_dialog_and_approval_does_not_claim_money_was_sent()
    {
        var (gw, _, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans","change_of_mind")).Ok);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        var vm=new GymMembership.Core.ViewModels.RefundsViewModel(svc) { DialogOpen=true };
        await vm.AutoRefreshAsync(); Assert.Empty(vm.Requests);
        vm.DialogOpen=false; await vm.OnAppearingAsync();
        await vm.DecideAsync(Assert.Single(vm.Requests),true,"Approve");
        var row=Assert.Single(vm.Requests);
        Assert.False(row.CanDecide); Assert.True(row.CanRepay);
        Assert.Contains("manual repayment",vm.Notice); Assert.Contains("no transfer",vm.Notice);
        Assert.Null(row.Request.PaidAt);
        await gw.OpenDrawerAsync();
        await vm.MarkPaidAsync(row,PayMethod.Cash,null);
        Assert.Equal("paid",Assert.Single(vm.Requests).Request.Status);
        Assert.False(Assert.Single(vm.Requests).CanRepay);
    }

    [Theory]
    [InlineData("service_not_provided")] [InlineData("misled")] [InlineData("contract_allows")] [InlineData("illness_or_relocation")] [InlineData("change_of_mind")]
    public async Task A_term_with_a_check_in_goes_to_review_with_a_reason_even_after_24_hours(string category)
    {
        var (gw, clock, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        await CheckIn(gw);
        clock.Now=clock.Now.AddHours(25);
        var card=Assert.Single((await svc.TermsAsync()).Value!);
        Assert.True(card.Activated); Assert.True(card.CanRequestRefund);
        Assert.Contains("review",card.RefundText);

        Assert.Equal("refund_reason_required",(await svc.RequestRefundAsync(card.Term.Id,"No category")).Message);
        Assert.Equal("bad_input",(await svc.RequestRefundAsync(card.Term.Id,"Bad","because")).Message);
        Assert.True((await svc.RequestRefundAsync(card.Term.Id,"Details",category)).Ok);
        var request=Assert.Single((await svc.RefundsAsync()).Value!).Request;
        Assert.Equal((true,category,p.Amount),(request.WasActivated,request.ReviewReason,request.Amount));
    }

    [Fact]
    public async Task A_term_that_has_ended_cannot_be_requested()
    {
        var (gw, clock, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        clock.Now=new DateTimeOffset(term.EndsAt,TimeSpan.Zero);
        var card=Assert.Single((await svc.TermsAsync()).Value!);
        Assert.False(card.CanRequestRefund);
        Assert.Equal("This term has ended.",card.RefundText);
        Assert.Equal("refund_window_closed",(await svc.RequestRefundAsync(term.Id,"Too late","misled")).Message);
    }

    [Fact]
    public async Task A_paid_term_with_no_check_in_is_a_full_refund_even_days_after_it_started()
    {
        var (gw, clock, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        clock.Now=clock.Now.AddDays(10);
        var card=Assert.Single((await svc.TermsAsync()).Value!);
        Assert.False(card.Activated);
        Assert.Contains("full refund",card.RefundText);
        Assert.True((await svc.RequestRefundAsync(card.Term.Id,"Never came")).Ok);
        var request=Assert.Single((await svc.RefundsAsync()).Value!).Request;
        Assert.Equal((false,(string?)null,p.Amount),(request.WasActivated,request.ReviewReason,request.Amount));
    }

    [Fact]
    public async Task A_queued_term_that_has_not_started_is_a_full_refund_with_no_category_and_no_time_limit()
    {
        var (gw, clock, svc, monthly, annual) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var next=(await svc.ExtendAsync(annual.Id)).Value!; await Verify(gw,next);
        clock.Now=clock.Now.AddDays(10);
        var queued=(await svc.TermsAsync()).Value!.Single(t=>t.Term.PaymentId==next.Id);
        Assert.False(queued.Activated); Assert.True(queued.CanRequestRefund);
        Assert.Contains("full refund",queued.RefundText);

        Assert.True((await svc.RequestRefundAsync(queued.Term.Id,"Plans changed")).Ok);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        var row=Assert.Single((await svc.RefundsAsync()).Value!);
        Assert.Equal((false,(string?)null,next.Amount),(row.Request.WasActivated,row.Request.ReviewReason,row.Request.Amount));
        Assert.Equal("No check-in during this term: full refund owed.",row.ReviewText);
        Assert.Equal("Full refund",row.AmountLabel);
        Assert.Equal(0,row.LaterTerms);
    }

    [Fact]
    public async Task Staff_see_why_a_used_term_needs_review_and_are_warned_about_later_paid_terms_which_stay()
    {
        var (gw, _, svc, monthly, annual) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        await CheckIn(gw);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        var next=(await svc.ExtendAsync(annual.Id)).Value!; await Verify(gw,next);
        Assert.True((await svc.RequestRefundAsync(term.Id,"The pool was closed","service_not_provided")).Ok);
        var me=gw.CurrentUserId!;

        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        var vm=new GymMembership.Core.ViewModels.RefundsViewModel(svc);
        await vm.OnAppearingAsync();
        var row=Assert.Single(vm.Requests);
        Assert.Equal("Checked in during this term, needs review: gym didn't provide the agreed service.",row.ReviewText);
        Assert.Equal("Refund requested",row.AmountLabel);
        Assert.Equal(1,row.LaterTerms);
        Assert.Equal("This member also has 1 later paid term. It stays and is not refunded.",row.LaterTermsWarning);

        await vm.DecideAsync(row,true,"Confirmed");   // item 11: approving cancels that member's term
        var terms=(await gw.ListAsync<MembershipTerm>(t=>t.MemberId==me));
        Assert.NotNull(terms.Single(t=>t.Id==term.Id).CancelledAt);
        Assert.Null(terms.Single(t=>t.PaymentId==next.Id).CancelledAt);
        Assert.Empty((await gw.ListAsync<MembershipTerm>()).Where(t=>t.MemberId!=me&&t.CancelledAt is not null));
    }

    [Fact]
    public void The_refund_reasons_have_words_for_members_and_for_staff()
    {
        Assert.Equal(5,RefundReasons.All.Count);
        Assert.All(RefundReasons.All,r=>{ Assert.NotEmpty(r.Member); Assert.NotEmpty(r.Staff); });
        Assert.Equal("change_of_mind",RefundReasons.KeyFor(RefundReasons.All.Last().Member));
        Assert.Null(RefundReasons.KeyFor("Something else"));
    }

    [Fact]
    public async Task An_administrator_cannot_decide_or_repay_their_own_refund_request()
    {
        var (gw, _, svc, monthly, _) = await Setup();
        var self=gw.CurrentUserId!;
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        Assert.True((await new AdminService(gw).AssignRoleAsync(self,"admin")).Ok);
        await gw.SignInAsync("phase7@demo.io","secret12");
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans","change_of_mind")).Ok);
        var request=(await gw.ListAsync<RefundRequest>()).Single();
        Assert.False((await svc.DecideAsync(request.Id,true,"Mine")).Ok);
        Assert.Equal("pending",(await gw.ListAsync<RefundRequest>()).Single().Status);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        Assert.True((await svc.DecideAsync(request.Id,true,"Approved by someone else")).Ok);
        await gw.SignInAsync("phase7@demo.io","secret12");
        Assert.False((await svc.MarkPaidAsync(request.Id,PayMethod.Cash,null)).Ok);
        Assert.Equal("approved",(await gw.ListAsync<RefundRequest>()).Single().Status);
        Assert.Contains("own refund",ErrorText.For(AppErrorKind.Validation,"self_decision"));
    }

    [Fact]
    public async Task A_free_payment_cannot_be_refunded()
    {
        var (gw, _, svc, _, _) = await Setup();
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        // D4: a payment is ₱0 only when a discount brought it there
        var trial=await gw.InsertAsync(new MembershipPackage {Name="Trial",Price=100,DurationDays=7,IsActive=true});
        await gw.InsertAsync(new Discount {Code="TRIALFREE",Percent=100,MaxUses=5,StartsAt=DateTime.UtcNow.AddMinutes(-1),ExpiresAt=DateTime.UtcNow.AddDays(1),IsActive=true});
        await gw.SignInAsync("phase7@demo.io","secret12");
        var p=(await svc.ExtendAsync(trial.Id,"TRIALFREE")).Value!; Assert.Equal(0m,p.Amount); await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        Assert.False((await svc.RequestRefundAsync(term.Id,"Changed plans","change_of_mind")).Ok);
    }

    sealed class NoPicker : IProofPicker { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string FileName, byte[] Data)?>(null); }

    [Fact]
    public async Task A_member_can_cancel_a_payment_nobody_verified_and_then_buy_again()
    {
        var (gw, _, svc, monthly, annual) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!;
        var item=(await new MembershipService(gw).MyPaymentsAsync()).Value!.Single();
        Assert.True(item.CanCancel);
        Assert.True((await svc.CancelPendingPaymentAsync(p.Id)).Ok);
        var after=(await new MembershipService(gw).MyPaymentsAsync()).Value!.Single();
        Assert.Equal(Status.Rejected,after.Status); Assert.False(after.CanCancel);
        Assert.True((await svc.ExtendAsync(annual.Id)).Ok);
    }

    [Fact]
    public async Task A_verified_or_foreign_payment_cannot_be_cancelled_this_way()
    {
        var (gw, _, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        Assert.False((await new MembershipService(gw).MyPaymentsAsync()).Value!.Single().CanCancel);
        Assert.False((await svc.CancelPendingPaymentAsync(p.Id)).Ok);
        await gw.SignUpAsync("other7@demo.io","secret12","Other","Person");
        var mine=(await svc.ExtendAsync(monthly.Id)).Value!;
        await gw.SignInAsync("phase7@demo.io","secret12");
        Assert.False((await svc.CancelPendingPaymentAsync(mine.Id)).Ok);
    }

    [Fact]
    public async Task The_membership_page_drops_the_pay_by_QR_panel_after_a_cancel()
    {
        var (gw, clock, svc, monthly, _) = await Setup();
        clock.Now = DateTimeOffset.UtcNow;   // the page judges a payment's expiry by the real clock, so a fixed date here expires it
        var p=(await svc.ExtendAsync(monthly.Id)).Value!;
        var vm=new GymMembership.Core.ViewModels.MyMembershipViewModel(new MembershipService(gw),new NoPicker(),svc);
        await vm.OnAppearingAsync();
        var item=Assert.IsType<PaymentItem>(vm.PayNow);
        await vm.CancelPaymentAsync(item);
        Assert.Null(vm.PayNow); Assert.Null(vm.ErrorMessage); Assert.Contains("cancelled",vm.Notice);
    }

    [Fact]
    public async Task Approving_a_refund_lowers_revenue_at_once_and_repayment_only_lowers_the_cash_drawer()
    {
        var (gw, clock, svc, monthly, _) = await Setup();
        clock.Now = DateTimeOffset.UtcNow.AddYears(1);
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans","change_of_mind")).Ok);
        var request=Assert.Single((await svc.RefundsAsync()).Value!).Request;
        var day=DateOnly.FromDateTime(clock.Now.UtcDateTime.AddHours(8));
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword); await gw.OpenDrawerAsync();
        async Task<decimal> Net()=>(await new AdminService(gw).RevenueAsync(day,day)).Value!.Sum(r=>r.Total);
        async Task<decimal> Cash()=>(await new PaymentStaffService(gw).ShiftAsync()).Value.Open!.Expected;   // the admin's drawer
        var received=await Net(); Assert.Equal(monthly.Price,received); var cash=await Cash();
        Assert.True((await svc.DecideAsync(request.Id,true,"Approved")).Ok);
        Assert.Equal(0m,await Net()); Assert.Equal(cash,await Cash());
        Assert.True((await svc.MarkPaidAsync(request.Id,PayMethod.Cash,null)).Ok);
        Assert.Equal(0m,await Net()); Assert.Equal(cash-monthly.Price,await Cash());
    }

    [Fact]
    public async Task Voiding_and_refunding_are_mutually_exclusive_and_direct_ledger_writes_are_denied()
    {
        var (gw, _, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans","change_of_mind")).Ok);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        Assert.False((await new PaymentStaffService(gw).VoidAsync(p.Id,"Wrong payment")).Ok);
        await Assert.ThrowsAnyAsync<Exception>(()=>gw.UpdateAsync(term));
        await Assert.ThrowsAnyAsync<Exception>(()=>gw.InsertAsync(new RefundRequest()));
    }
}
