using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>The payment rules of docs/specs/database-plan.md section 8, against the demo backend.</summary>
public class PaymentTests
{
    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    static async Task<MembershipPackage> Package(DemoGateway gw, string name) =>
        (await new MembershipService(gw).ListPackagesAsync()).Value!.First(p => p.Name == name);

    // ---------- QR flow ----------
    [Fact]
    public async Task QR_payment_end_to_end_from_guest_to_member_with_a_receipt()
    {
        var gw = new DemoGateway();
        var auth = new AuthService(gw);
        Assert.True((await auth.RegisterAsync("Ana Reyes", "ana@demo.io", "secret12")).Ok);
        var member = new MembershipService(gw);

        // 1. a pending payment with a code to type into the e-wallet message and a deadline
        var pay = (await member.AvailAsync((await Package(gw, "Quarterly")).Id)).Value!;
        Assert.Matches("^GYM-[A-Z2-9]{5}$", pay.PaymentCode);
        Assert.Equal((PayMethod.Qr, Status.Pending, 4000m), (pay.Method, pay.Status, pay.Amount));
        Assert.InRange((pay.ExpiresAt!.Value - DateTime.UtcNow).TotalHours, 47, 48);
        var item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.True(item.NeedsProof);

        // 2. the member pays, then sends the reference number and a screenshot
        Assert.Equal(AppErrorKind.Validation, (await member.SubmitProofAsync(pay.Id, "  ", "slip.png", [1])).Error);
        Assert.True((await member.SubmitProofAsync(pay.Id, "GC-8841", "slip.png", [1, 2, 3])).Ok);
        item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.True(item.AwaitingReview);
        Assert.False(item.NeedsProof);

        // 3. the front desk sees it with its reference number, approves, and the plan switches on
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var staff = new PaymentStaffService(gw);
        var row = (await staff.PendingAsync()).Value!.First(r => r.Payment.Id == pay.Id);
        Assert.Equal(("Ana Reyes", "Quarterly", "GC-8841", true), (row.Payer, row.Item, row.Payment.ReferenceNo, row.HasProof));
        Assert.True((await staff.VerifyAsync(pay.Id, true)).Ok);
        Assert.Equal(AppErrorKind.Conflict, (await staff.VerifyAsync(pay.Id, true)).Error);

        await gw.SignInAsync("ana@demo.io", "secret12");
        item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.Matches(@"^OR-\d{4}-\d{6}$", item.ReceiptNo);
        var plan = (await member.MyMembershipsAsync()).Value!.Single();
        Assert.Equal(("Quarterly", "active"), (plan.PackageName, plan.Status));
        Assert.InRange(plan.DaysLeft!.Value, 89, 90);
        Assert.True((await member.AccessAsync()).Value!.Active);
    }

    [Fact]
    public async Task A_reference_number_cannot_be_used_on_two_payments()
    {
        var gw = await As("carlo@demo.io");
        var member = new MembershipService(gw);
        var pay = (await member.AvailAsync((await Package(gw, "Monthly")).Id)).Value!;
        Assert.Equal(AppErrorKind.Conflict, (await member.SubmitProofAsync(pay.Id, "GC-20260412-889", "x.png", [1])).Error);   // Bea's seeded reference
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_is_final_and_the_member_can_pay_again()
    {
        var gw = await As("employee@demo.io");
        var staff = new PaymentStaffService(gw);
        var beas = (await staff.PendingAsync()).Value!.Single(r => r.Payer == "Bea Lim");
        Assert.Equal(AppErrorKind.Validation, (await staff.VerifyAsync(beas.Payment.Id, false, "  ")).Error);
        Assert.True((await staff.VerifyAsync(beas.Payment.Id, false, "Amount does not match")).Ok);
        Assert.Equal(AppErrorKind.Conflict, (await staff.VerifyAsync(beas.Payment.Id, true)).Error);   // final

        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var member = new MembershipService(gw);
        var item = (await member.MyPaymentsAsync()).Value!.Single();
        Assert.Equal(("rejected", "Amount does not match", true), (item.Status, item.RejectedReason, item.CanPayAgain));
        Assert.Empty((await member.MyMembershipsAsync()).Value!);   // nothing was switched on

        var again = (await member.PayAgainAsync(item)).Value!;       // a fresh payment for the same package
        Assert.NotEqual(beas.Payment.Id, again.Id);
        Assert.Equal(beas.Payment.Amount, again.Amount);
        Assert.Equal(AppErrorKind.Conflict, (await member.PayAgainAsync(item)).Error);   // one pending membership at a time
    }

    // ---------- the counter ----------
    [Fact]
    public async Task Cash_at_the_counter_is_verified_at_once_with_a_receipt_and_switches_the_plan_on()
    {
        var gw = await As("employee@demo.io");
        var staff = new PaymentStaffService(gw);
        var monthly = await Package(gw, "Monthly");

        // Carlo's plan expired, so a new membership can be bought for him
        var paid = (await staff.RecordCounterAsync(PayKind.Membership, PayMethod.Cash, "u-carlo", monthly.Id, null, null, null)).Value!;
        Assert.Equal((Status.Verified, 1500m, "u-emp"), (paid.Status, paid.Amount, paid.VerifiedBy));
        Assert.Matches(@"^OR-\d{4}-\d{6}$", paid.ReceiptNo);

        await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new MembershipService(gw).AccessAsync()).Value!.Active);
    }

    [Fact]
    public async Task The_counter_refuses_QR_missing_references_and_staff_paying_for_themselves()
    {
        var gw = await As("employee@demo.io");
        var staff = new PaymentStaffService(gw);
        var monthly = await Package(gw, "Monthly");
        Assert.Equal(AppErrorKind.Validation, (await staff.RecordCounterAsync(PayKind.Membership, PayMethod.Qr, "u-carlo", monthly.Id, null, null, null)).Error);
        Assert.Equal(AppErrorKind.Validation, (await staff.RecordCounterAsync(PayKind.Membership, PayMethod.Gcash, "u-carlo", monthly.Id, null, " ", null)).Error);
        Assert.True((await staff.RecordCounterAsync(PayKind.Membership, PayMethod.Gcash, "u-carlo", monthly.Id, null, "GC-1001", null)).Ok);
        Assert.Equal(AppErrorKind.Validation, (await staff.RecordCounterAsync(PayKind.Membership, PayMethod.Cash, "u-emp", monthly.Id, null, null, null)).Error);

        await As("member@demo.io", gw);
        Assert.Equal(AppErrorKind.Unauthorized, (await staff.RecordCounterAsync(PayKind.Membership, PayMethod.Cash, "u-carlo", monthly.Id, null, null, null)).Error);
    }

    [Fact]
    public async Task A_walk_in_takes_the_default_price_creates_a_check_in_and_needs_no_member()
    {
        var gw = await As("employee@demo.io");
        var staff = new PaymentStaffService(gw);
        Assert.Equal(AppErrorKind.Validation, (await staff.RecordCounterAsync(PayKind.WalkIn, PayMethod.Cash, null, null, "  ", null, null)).Error);

        var paid = (await staff.RecordCounterAsync(PayKind.WalkIn, PayMethod.Cash, null, null, "Pedro Ramos", null, null)).Value!;
        Assert.Equal((150m, "Pedro Ramos", null), (paid.Amount, paid.WalkInName, paid.MemberId));
        Assert.Single(await gw.ListAsync<GymCheckIn>(c => c.PaymentId == paid.Id));

        var cash = (await staff.CashSummaryAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)))).Value!;
        Assert.Contains(cash, r => r.Kind == PayKind.WalkIn && r.Total >= 150m);
    }

    [Fact]
    public async Task The_counter_lists_what_can_be_sold_including_a_members_renewals()
    {
        var gw = await As("employee@demo.io");
        var staff = new PaymentStaffService(gw);
        Assert.Contains((await staff.ItemsAsync(PayKind.Membership, null)).Value!, i => i.Label.StartsWith("Monthly"));
        Assert.DoesNotContain((await staff.ItemsAsync(PayKind.Amenity, null)).Value!, i => i.Label.StartsWith("Boxing"));   // closed
        Assert.Single((await staff.ItemsAsync(PayKind.Renewal, "u-carlo")).Value!);
        Assert.Empty((await staff.ItemsAsync(PayKind.Renewal, "u-bea")).Value!);   // nothing to renew yet
    }

    // ---------- voids ----------
    [Fact]
    public async Task Only_an_admin_voids_with_a_reason_and_the_plan_it_switched_on_is_reversed()
    {
        var gw = await As("employee@demo.io");
        var staff = new PaymentStaffService(gw);
        var monthly = await Package(gw, "Monthly");
        var paid = (await staff.RecordCounterAsync(PayKind.Membership, PayMethod.Cash, "u-carlo", monthly.Id, null, null, null)).Value!;
        Assert.Equal(AppErrorKind.Unauthorized, (await staff.VoidAsync(paid.Id, "mistake")).Error);   // front desk can't void

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Validation, (await staff.VoidAsync(paid.Id, " ")).Error);
        Assert.True((await staff.VoidAsync(paid.Id, "Wrong plan")).Ok);
        Assert.Equal(AppErrorKind.Conflict, (await staff.VoidAsync(paid.Id, "again")).Error);

        Assert.Contains(await gw.ListAsync<AuditEntry>(), a => a.Action == "payment.void" && a.EntityId == paid.Id.ToString());
        await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword);
        Assert.False((await new MembershipService(gw).AccessAsync()).Value!.Active);
        Assert.Contains((await new MembershipService(gw).MyPaymentsAsync()).Value!, p => p.Id == paid.Id && p.Status == Status.Voided);
    }

    [Fact]
    public async Task Voiding_a_renewal_restores_the_old_end_date_and_voided_money_leaves_the_revenue()
    {
        var gw = await As("employee@demo.io");
        var staff = new PaymentStaffService(gw);
        var alexPlan = (await gw.ListAsync<UserMembershipPackage>(m => m.MemberId == "u-alex" && m.Status == "active")).Single();
        var before = alexPlan.EndsAt!.Value;

        var paid = (await staff.RecordCounterAsync(PayKind.Renewal, PayMethod.Cash, "u-alex", alexPlan.Id, null, null, null)).Value!;
        Assert.True((await gw.ListAsync<UserMembershipPackage>(m => m.Id == alexPlan.Id)).Single().EndsAt > before.AddDays(29));

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var admin = new AdminService(gw);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        decimal Total() => admin.RevenueAsync(today.AddDays(-60), today.AddDays(1)).Result.Value!.Sum(r => r.Total);
        var withRenewal = Total();

        await staff.VoidAsync(paid.Id, "Taken twice");
        Assert.Equal(before, (await gw.ListAsync<UserMembershipPackage>(m => m.Id == alexPlan.Id)).Single().EndsAt);
        Assert.Equal(withRenewal - 1500m, Total());
    }

    // ---------- guest lock ----------
    [Fact]
    public async Task A_guest_cannot_buy_amenities_hire_coaches_book_sessions_or_chat()
    {
        var gw = new DemoGateway();
        await new AuthService(gw).RegisterAsync("Guest User", "guest@demo.io", "secret12");

        Assert.Equal(AppErrorKind.Validation, (await new AmenityService(gw).AvailAsync(1)).Error);
        Assert.Equal(AppErrorKind.Validation, (await new CoachService(gw).HireAsync("u-coach1")).Error);
        var t = DateTime.UtcNow.AddDays(3);
        Assert.Equal(AppErrorKind.Validation, (await new SessionService(gw).RequestAsync("u-coach1", t, t.AddHours(1), null)).Error);
        Assert.Equal(AppErrorKind.Unauthorized, (await new MessageService(gw).SendAsync("u-coach1", gw.CurrentUserId!, "hello")).Error);

        // but they can see the catalogue and the coaches
        Assert.NotEmpty((await new MembershipService(gw).ListPackagesAsync()).Value!);
        Assert.NotEmpty((await new CoachService(gw).ListCoachesAsync()).Value!);
    }

    [Fact]
    public async Task An_amenity_is_bought_by_QR_like_a_package_and_opens_for_use_once_verified()
    {
        var gw = await As("member@demo.io");
        var amenities = new AmenityService(gw);
        var sauna = (await amenities.CatalogAsync()).Value!.First(a => a.Amenity.Name == "Sauna");
        Assert.True(sauna.CanBuy);
        var pay = (await amenities.AvailAsync(sauna.Amenity.Id)).Value!;
        Assert.Equal((PayKind.Amenity, 200m), (pay.Kind, pay.Amount));
        Assert.True((await amenities.CatalogAsync()).Value!.First(a => a.Amenity.Name == "Sauna").IsPending);
        Assert.Equal(AppErrorKind.Conflict, (await amenities.AvailAsync(sauna.Amenity.Id)).Error);

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        await new PaymentStaffService(gw).VerifyAsync(pay.Id, true);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var now = (await amenities.CatalogAsync()).Value!.First(a => a.Amenity.Name == "Sauna");
        Assert.True(now.IsActive);
        Assert.True((await amenities.LogUsageAsync(now.UserAmenityId!.Value, null)).Ok);

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var usage = (await amenities.PendingUsagesAsync()).Value!.First(u => u.Amenity == "Sauna");
        Assert.True((await amenities.VerifyUsageAsync(usage.Usage.Id, true)).Ok);
    }

    // ---------- gym settings ----------
    [Fact]
    public async Task Only_the_admin_changes_the_gym_settings()
    {
        var gw = await As("employee@demo.io");
        var admin = new AdminService(gw);
        var settings = (await admin.GymSettingsAsync()).Value!;
        settings.WalkInPrice = 200;
        Assert.Equal(AppErrorKind.Unauthorized, (await admin.SaveGymSettingsAsync(settings, null)).Error);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        settings.GymName = " ";
        Assert.Equal(AppErrorKind.Validation, (await admin.SaveGymSettingsAsync(settings, null)).Error);
        settings.GymName = "Iron House";
        Assert.True((await admin.SaveGymSettingsAsync(settings, ("qr.png", [1, 2]))).Ok);

        var saved = (await admin.GymSettingsAsync()).Value!;
        Assert.Equal(("Iron House", 200m), (saved.GymName, saved.WalkInPrice));
        Assert.EndsWith("qr.png", saved.QrImagePath);
    }
}
