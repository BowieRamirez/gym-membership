using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class SubscriptionTests
{
    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 17, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static async Task<(DemoGateway Gw, Clock Clock, SubscriptionService Svc, MembershipPackage Monthly, MembershipPackage Annual)> Setup()
    {
        var clock = new Clock(); var gw = new DemoGateway(clock: clock);
        await gw.SignUpAsync("phase7@demo.io", "secret12", "Ana Reyes");
        var packages = await gw.ListAsync<MembershipPackage>();
        return (gw, clock, new SubscriptionService(gw), packages.First(p => p.Name == "Monthly"), packages.First(p => p.Name == "Annual"));
    }
    static async Task Verify(DemoGateway gw, Payment pay)
    {
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new PaymentStaffService(gw).VerifyAsync(pay.Id, true)).Ok);
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
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans")).Ok);
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
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans")).Ok);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        var vm=new GymMembership.Core.ViewModels.RefundsViewModel(svc) { DialogOpen=true };
        await vm.AutoRefreshAsync(); Assert.Empty(vm.Requests);
        vm.DialogOpen=false; await vm.OnAppearingAsync();
        await vm.DecideAsync(Assert.Single(vm.Requests),true,"Approve");
        var row=Assert.Single(vm.Requests);
        Assert.False(row.CanDecide); Assert.True(row.CanRepay);
        Assert.Contains("manual repayment",vm.Notice); Assert.Contains("no transfer",vm.Notice);
        Assert.Null(row.Request.PaidAt);
        await vm.MarkPaidAsync(row,PayMethod.Cash,null);
        Assert.Equal("paid",Assert.Single(vm.Requests).Request.Status);
        Assert.False(Assert.Single(vm.Requests).CanRepay);
    }

    [Theory]
    [InlineData(86399,true)] [InlineData(86400,false)] [InlineData(86401,false)]
    public async Task Refund_window_ends_at_exactly_24_hours_after_verification(int seconds,bool accepted)
    {
        var (gw, clock, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        clock.Now=clock.Now.AddSeconds(seconds);
        Assert.Equal(accepted,(await svc.RequestRefundAsync(term.Id,"Changed plans")).Ok);
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
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans")).Ok);
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
        var free=await gw.InsertAsync(new MembershipPackage {Name="Free trial",Price=0,DurationDays=7,IsActive=true});
        await gw.SignInAsync("phase7@demo.io","secret12");
        var p=(await svc.ExtendAsync(free.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        Assert.False((await svc.RequestRefundAsync(term.Id,"Changed plans")).Ok);
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
        await gw.SignUpAsync("other7@demo.io","secret12","Other Person");
        var mine=(await svc.ExtendAsync(monthly.Id)).Value!;
        await gw.SignInAsync("phase7@demo.io","secret12");
        Assert.False((await svc.CancelPendingPaymentAsync(mine.Id)).Ok);
    }

    [Fact]
    public async Task The_membership_page_drops_the_pay_by_QR_panel_after_a_cancel()
    {
        var (gw, _, svc, monthly, _) = await Setup();
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
        var (gw, _, svc, monthly, _) = await Setup();
        var p=(await svc.ExtendAsync(monthly.Id)).Value!; await Verify(gw,p);
        var term=Assert.Single((await svc.TermsAsync()).Value!).Term;
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans")).Ok);
        var request=Assert.Single((await svc.RefundsAsync()).Value!).Request;
        var day=new DateOnly(2026,10,17);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        async Task<decimal> Net()=>(await new AdminService(gw).RevenueAsync(day,day)).Value!.Sum(r=>r.Total);
        async Task<decimal> Cash()=>(await new PaymentStaffService(gw).CashSummaryAsync(day)).Value!.Sum(r=>r.Total);
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
        Assert.True((await svc.RequestRefundAsync(term.Id,"Changed plans")).Ok);
        await gw.SignInAsync("admin@demo.io",DemoGateway.DemoPassword);
        Assert.False((await new PaymentStaffService(gw).VoidAsync(p.Id,"Wrong payment")).Ok);
        await Assert.ThrowsAnyAsync<Exception>(()=>gw.UpdateAsync(term));
        await Assert.ThrowsAnyAsync<Exception>(()=>gw.InsertAsync(new RefundRequest()));
    }
}
