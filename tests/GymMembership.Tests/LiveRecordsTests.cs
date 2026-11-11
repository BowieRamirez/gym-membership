using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

// Phase 7 (F4, 2026-11-11): pages that show payments, check-ins or membership terms reload when one changes. The demo gateway raises the
// same hint the real backend sends, only when one of those rows really changed, so a page reloading itself never sets off another reload.
public class LiveRecordsTests
{
    static async Task<DemoGateway> Desk()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        return gw;
    }

    [Fact]
    public async Task A_check_in_raises_the_hint_and_reading_does_not()
    {
        var gw = await Desk();
        var live = new DemoLiveRecords(gw);
        var heard = 0;
        live.Changed += () => heard++;
        var attendance = new AttendanceService(gw);
        await attendance.CheckInsOnAsync(DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8)));   // a read: nothing changed
        await new PaymentStaffService(gw).PendingAsync();
        Assert.Equal(0, heard);
        Assert.True((await attendance.CheckInAsync("u-alex", "manual", pin: "1234")).Ok);
        Assert.Equal(1, heard);
    }

    [Fact]
    public async Task A_failed_check_in_raises_nothing()
    {
        var gw = await Desk();
        var live = new DemoLiveRecords(gw);
        var heard = 0;
        live.Changed += () => heard++;
        Assert.False((await new AttendanceService(gw).CheckInAsync("u-alex", "manual", pin: "0000")).Ok);   // wrong PIN: no row
        Assert.Equal(0, heard);
    }

    [Fact]
    public async Task A_page_reloads_on_a_hint_and_a_burst_during_a_reload_costs_one_more()
    {
        var live = new FakeLive();
        var reloads = 0;
        var gate = new TaskCompletionSource();
        using var refresh = new LiveRefresh(live, async () => { reloads++; if (reloads == 1) await gate.Task; });
        live.Raise();                       // starts reload 1, which waits
        live.Raise(); live.Raise(); live.Raise();   // three more while it runs: one follow-up, not three
        gate.SetResult();
        await refresh.Idle;
        Assert.Equal(2, reloads);
    }

    [Fact]
    public async Task A_page_that_went_away_hears_nothing()
    {
        var live = new FakeLive();
        var reloads = 0;
        var refresh = new LiveRefresh(live, () => { reloads++; return Task.CompletedTask; });
        live.Raise();
        await refresh.Idle;
        refresh.Dispose();
        live.Raise();
        Assert.Equal(1, reloads);
    }

    [Fact]
    public async Task A_failed_reload_does_not_stop_the_next_one()
    {
        var live = new FakeLive();
        var reloads = 0;
        using var refresh = new LiveRefresh(live, () => { if (++reloads == 1) throw new InvalidOperationException("offline"); return Task.CompletedTask; });
        live.Raise();
        await refresh.Idle;
        live.Raise();
        await refresh.Idle;
        Assert.Equal(2, reloads);
    }

    // F4 as decided (2026-11-11): these pages hear the hint; all but the Overview (which has its own line) show "Updated" with Refresh
    [Theory]
    [InlineData("Views/MyMembershipPage", true)]
    [InlineData("Views/HomePage", true)]
    [InlineData("Views/VerifyPaymentsPage", true)]
    [InlineData("Views/CheckInPage", true)]
    [InlineData("Views/AdminOverviewPage.Windows", false)]
    public void The_decided_pages_listen_live(string page, bool hasBar)
    {
        var code = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), page + (hasBar ? ".xaml.cs" : ".cs")));
        Assert.Contains("protected override bool LiveRecords => true;", code);
        if (!hasBar) return;
        Assert.Contains("protected override Controls.UpdatedBar? Updated => UpdatedLine;", code);
        Assert.Contains("<c:UpdatedBar x:Name=\"UpdatedLine\"", File.ReadAllText(Path.Combine(TestPaths.AppFolder(), page + ".xaml")));
    }

    sealed class FakeLive : ILiveRecords
    {
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
        public Task StartAsync() => Task.CompletedTask;
    }
}
