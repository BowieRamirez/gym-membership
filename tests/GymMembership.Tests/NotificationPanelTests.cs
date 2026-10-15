using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class NotificationPanelTests
{
    static async Task<(DemoGateway Gw, NotificationService Svc, NotificationsViewModel Vm)> Open()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var svc = new NotificationService(gw);
        var vm = new NotificationsViewModel(svc);
        await vm.OnAppearingAsync();
        return (gw, svc, vm);
    }

    [Fact]
    public async Task Opening_lists_newest_first_and_counts_the_unread()
    {
        var (_, _, vm) = await Open();
        Assert.Equal(vm.Items.OrderByDescending(n => n.CreatedAt).Select(n => n.Id), vm.Items.Select(n => n.Id));
        Assert.Equal(vm.Items.Count(n => n.ReadAt is null), vm.UnreadCount);
        Assert.True(vm.HasUnread);
        Assert.True(vm.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Tapping_an_unread_alert_reads_it_and_the_list_redraws()
    {
        var (_, _, vm) = await Open();
        var before = vm.UnreadCount;
        var unread = vm.Items.First(n => n.ReadAt is null);

        await vm.MarkReadCommand.ExecuteAsync(unread);
        await Task.Delay(100);   // the redraw is triggered by the service's change notice

        Assert.Equal(before - 1, vm.UnreadCount);
        Assert.NotNull(vm.Items.First(n => n.Id == unread.Id).ReadAt);
    }

    [Fact]
    public async Task Mark_all_read_empties_the_unread_count_and_switches_itself_off()
    {
        var (_, _, vm) = await Open();
        await vm.MarkAllReadCommand.ExecuteAsync(null);
        await Task.Delay(100);

        Assert.Equal(0, vm.UnreadCount);
        Assert.All(vm.Items, n => Assert.NotNull(n.ReadAt));
        Assert.False(vm.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public async Task Offline_shows_a_plain_message_and_leaves_everything_unread()
    {
        var (gw, _, vm) = await Open();
        var before = vm.UnreadCount;
        gw.FailWith = new HttpRequestException("offline");

        await vm.MarkAllReadCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.Equal(before, vm.UnreadCount);
    }

    [Fact]
    public async Task A_change_made_elsewhere_refreshes_an_open_panel_and_a_disposed_one_ignores_it()
    {
        var (_, svc, vm) = await Open();
        await svc.MarkAllReadAsync();
        await Task.Delay(100);
        Assert.Equal(0, vm.UnreadCount);

        var (_, svc2, vm2) = await Open();
        var before = vm2.UnreadCount;
        vm2.Dispose();
        await svc2.MarkAllReadAsync();
        await Task.Delay(100);
        Assert.Equal(before, vm2.UnreadCount);   // closed panel: it no longer redraws
    }

    // a notification service whose list answers the test releases by hand
    sealed class SlowList : INotificationService
    {
        public readonly Queue<TaskCompletionSource<AppResult<IReadOnlyList<GymMembership.Core.Models.UserNotification>>>> Pending = new();
        public event Action? Changed;
        public void Raise() => Changed?.Invoke();
        public Task<AppResult<IReadOnlyList<GymMembership.Core.Models.UserNotification>>> ListAsync()
        { var t = new TaskCompletionSource<AppResult<IReadOnlyList<GymMembership.Core.Models.UserNotification>>>(); Pending.Enqueue(t); return t.Task; }
        public Task<AppResult<int>> UnreadCountAsync() => throw new NotSupportedException();
        public Task<AppResult<bool>> MarkReadAsync(int id) => throw new NotSupportedException();
        public Task<AppResult<bool>> MarkAllReadAsync() => throw new NotSupportedException();
    }

    static AppResult<IReadOnlyList<GymMembership.Core.Models.UserNotification>> Rows(params bool[] unread) =>
        AppResult<IReadOnlyList<GymMembership.Core.Models.UserNotification>>.Success(unread.Select((u, i) =>
            new GymMembership.Core.Models.UserNotification { Id = i + 1, Title = $"n{i}", CreatedAt = DateTime.UtcNow.AddMinutes(-i), ReadAt = u ? null : DateTime.UtcNow }).ToList());

    [Fact]
    public async Task A_slow_older_reload_cannot_bring_back_an_alert_that_was_just_read()
    {
        var svc = new SlowList();
        var vm = new NotificationsViewModel(svc);
        svc.Raise();   // reload 1 starts
        svc.Raise();   // reload 2 starts (a tap happened in between)
        var first = svc.Pending.Dequeue();
        var second = svc.Pending.Dequeue();

        second.SetResult(Rows(false, false));   // newer answer: everything read
        await Task.Delay(50);
        first.SetResult(Rows(true, false));     // older answer arrives last: one still unread
        await Task.Delay(50);

        Assert.Equal(0, vm.UnreadCount);
        Assert.All(vm.Items, n => Assert.NotNull(n.ReadAt));
    }

    [Fact]
    public async Task Reading_alerts_replaces_only_the_rows_that_changed_so_the_list_does_not_jump_to_the_top()
    {
        var (_, _, vm) = await Open();
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);
        var unread = vm.Items.Count(n => n.ReadAt is null);
        Assert.True(unread > 0);

        await vm.MarkAllReadCommand.ExecuteAsync(null);
        await Task.Delay(100);

        Assert.Equal(unread, actions.Count);
        Assert.All(actions, a => Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Replace, a));   // no Reset, no Clear and re-add
    }
}
