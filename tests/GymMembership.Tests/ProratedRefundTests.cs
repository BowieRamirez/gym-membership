using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// G22 (0067): a membership refund is only the unused days, counted when staff approve. G23 (0068): asked only in the term's first 7 days.
public class ProratedRefundTests
{
    static readonly DateTime S = new(2026, 10, 17, 4, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(-60, 0)]                        // not started
    [InlineData(0, 0)]                          // the very start
    [InlineData(1, 1)]                          // a started day counts as used
    [InlineData(14 * 86400, 14)]
    [InlineData(14 * 86400 + 1, 15)]
    [InlineData(40 * 86400, 30)]                // never more than the term
    public void Days_used_round_a_started_day_up(int seconds, int used) =>
        Assert.Equal(used, RefundMath.DaysUsed(S, 30, S.AddSeconds(seconds)));

    [Theory]
    [InlineData(0, 1450)]                       // day 1 has started
    [InlineData(6, 1150)]                       // 7 used
    [InlineData(8, 1050)]                       // asked on day 7, approved on day 9
    public void Due_is_the_days_left_of_what_was_paid(int fullDays, decimal due) =>
        Assert.Equal(due, RefundMath.Due(1500, 30, S, S.AddDays(fullDays).AddMinutes(1)));

    [Fact]
    public void A_term_not_started_gives_everything_back() => Assert.Equal(1500m, RefundMath.Due(1500, 30, S, S.AddMinutes(-1)));

    [Fact]
    public void A_discounted_payment_is_split_and_rounded_to_the_centavo()
    {
        Assert.Equal(1035m, RefundMath.Due(1350, 30, S, S.AddDays(6).AddMinutes(1)));
        Assert.Equal(966.67m, RefundMath.Due(1000, 30, S, S.AddMinutes(1)));
    }

    [Fact]
    public void The_last_moment_to_ask_is_7_days_after_the_start_whatever_the_term()
    {
        Assert.Equal(S.AddDays(7), RefundMath.LastDayToAsk(S));
        Assert.False(RefundMath.WeekPassed(S, S.AddDays(7)));
        Assert.True(RefundMath.WeekPassed(S, S.AddDays(7).AddSeconds(1)));
        Assert.False(RefundMath.WeekPassed(S, S.AddDays(-30)));   // bought ahead, not started
    }

    [Fact]
    public async Task Demo_refunds_the_days_left_at_approval_even_after_the_week()
    {
        var (gw, clock, svc, monthly, _) = await SubscriptionTests.Setup();
        var p = (await svc.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var start = clock.Now;

        clock.Now = start.AddDays(6).AddMinutes(1);   // day 7
        var card = Assert.Single((await svc.TermsAsync()).Value!);
        Assert.True(card.CanRequestRefund);
        Assert.Equal(1150m, card.RefundEstimate);
        Assert.Equal(start.UtcDateTime.AddDays(7), card.LastDayToAsk);
        Assert.True((await svc.RequestRefundAsync(card.Term.Id, "Moving away")).Ok);

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var vm = new RefundsViewModel(svc); await vm.OnAppearingAsync();
        Assert.Equal("Estimated refund", Assert.Single(vm.Requests).AmountLabel);
        clock.Now = start.AddDays(8).AddMinutes(1);   // approved on day 9
        await vm.DecideAsync(vm.Requests[0], true, "Ok");
        var row = Assert.Single(vm.Requests);
        Assert.Equal((1050m, "Refund"), (row.Request.Amount, row.AmountLabel));
    }

    [Fact]
    public async Task Demo_refuses_a_request_after_the_first_week_and_an_approval_after_the_term_ended()
    {
        var (gw, clock, svc, monthly, _) = await SubscriptionTests.Setup();
        var p = (await svc.ExtendAsync(monthly.Id)).Value!; await SubscriptionTests.Verify(gw, p);
        var start = clock.Now;
        clock.Now = start.AddDays(7).AddMinutes(1);   // day 8
        var card = Assert.Single((await svc.TermsAsync()).Value!);
        Assert.False(card.CanRequestRefund);
        Assert.Equal("Refunds are only for a term's first 7 days, and this one is past that.", card.RefundNote);   // the phone row says why the button is gone
        Assert.Equal(card.RefundNote, card.RefundText);
        var refused = await svc.RequestRefundAsync(card.Term.Id, "Too late");
        Assert.Equal("Refunds are only for a term's first 7 days, and this one is past that.", ErrorText.For(refused.Error, refused.Message));

        clock.Now = start.AddDays(2);
        Assert.True((await svc.RequestRefundAsync(card.Term.Id, "Moving away")).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        clock.Now = start.AddDays(31);
        var request = Assert.Single((await svc.RefundsAsync()).Value!).Request;
        var ended = await svc.DecideAsync(request.Id, true, "Ok");
        Assert.Equal("This term has ended, so there is nothing left to refund. Reject the request instead.", ErrorText.For(ended.Error, ended.Message));
    }
}
