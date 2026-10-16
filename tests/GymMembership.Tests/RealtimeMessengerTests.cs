using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class RealtimeMessengerTests
{
    // a stand-in for Supabase Realtime that the test drives by hand
    public sealed class FakeRealtime : IRealtimeChat
    {
        public bool Connected { get; private set; }
        public event Action? ConnectionChanged;
        public event Action? Changed;
        public event Action<string>? Typing;
        public event Action<IReadOnlySet<string>>? OnlineChanged;
        public List<string> Started = [];
        public List<string?> Watched = [];
        public List<string> TypingSent = [];
        public int Stops;
        public Exception? FailStart;
        public Task StartAsync(string userId)
        {
            Started.Add(userId);
            if (FailStart is not null) throw FailStart;
            SetConnected(true);
            return Task.CompletedTask;
        }
        public Task StopAsync() { Stops++; SetConnected(false); return Task.CompletedTask; }
        public Task WatchAsync(string? key) { Watched.Add(key); return Task.CompletedTask; }
        public Task SendTypingAsync(string key) { TypingSent.Add(key); return Task.CompletedTask; }
        public void SetConnected(bool on) { if (Connected == on) return; Connected = on; ConnectionChanged?.Invoke(); }
        public void RaiseChanged() => Changed?.Invoke();
        public void RaiseTyping(string key) => Typing?.Invoke(key);
        public void RaiseOnline(params string[] ids) => OnlineChanged?.Invoke(new HashSet<string>(ids));
        public bool HasListeners => Changed is not null || Typing is not null || OnlineChanged is not null || ConnectionChanged is not null;
    }

    // counts how often the list is asked for, to see how many reloads a burst of signals causes
    public sealed class CountingChat(IChatService inner) : IChatService
    {
        public int ThreadCalls;
        public (string CoachId, string? MemberId)? Pending { get => inner.Pending; set => inner.Pending = value; }
        public Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync() { ThreadCalls++; return inner.ThreadsAsync(); }
        public Task<AppResult<ChatPage>> MessagesAsync(ChatThread t, long? beforeId = null, int limit = 50) => inner.MessagesAsync(t, beforeId, limit);
        public Task<AppResult<bool>> SendAsync(ChatThread t, string body) => inner.SendAsync(t, body);
        public Task<AppResult<bool>> SendPhotoAsync(ChatThread t, string fileName, byte[] data, string caption) => inner.SendPhotoAsync(t, fileName, data, caption);
        public Task<AppResult<bool>> MarkReadAsync(ChatThread t) => inner.MarkReadAsync(t);
        public Task<AppResult<bool>> MarkDeliveredAsync() => inner.MarkDeliveredAsync();
        public Task<AppResult<bool>> ClaimAsync(ChatThread t, bool claim) => inner.ClaimAsync(t, claim);
        public Task<AppResult<bool>> SetOpenAsync(ChatThread t, bool open) => inner.SetOpenAsync(t, open);
        public Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string query) => inner.SearchAsync(query);
    }

    public static async Task<(DemoGateway Gw, CountingChat Chat, FakeRealtime Rt, MessengerViewModel Vm)> Open(string email = "member@demo.io", DemoGateway? gw = null, FakeRealtime? rt = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        rt ??= new FakeRealtime();
        var chat = new CountingChat(new ChatService(gw, new NotificationService(gw)));
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw), new DemoPhotoPicker(), rt)
        { ChangeDelay = TimeSpan.FromMilliseconds(20), TypingHold = TimeSpan.FromMilliseconds(80) };
        await vm.OnAppearingAsync();
        return (gw, chat, rt, vm);
    }

    [Fact]
    public async Task Opening_the_page_starts_realtime_for_that_user_and_a_connected_page_polls_slowly()
    {
        var (gw, _, rt, vm) = await Open();
        Assert.Equal(new[] { gw.CurrentUserId! }, rt.Started);
        Assert.Equal(TimeSpan.FromSeconds(30), vm.RefreshEvery);
    }

    [Fact]
    public async Task Without_a_connection_the_page_polls_every_6_seconds_and_says_nothing_about_it()
    {
        var rt = new FakeRealtime { FailStart = new Exception("no network") };
        var (_, _, _, vm) = await Open(rt: rt);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.RefreshEvery);
        Assert.Null(vm.ErrorMessage);
        Assert.NotEmpty(vm.Threads);   // Messages loaded all the same
    }

    [Fact]
    public async Task The_interval_follows_the_connection()
    {
        var (_, _, rt, vm) = await Open();
        var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        rt.SetConnected(false);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.RefreshEvery);
        Assert.Contains(nameof(vm.RefreshEvery), changed);
    }

    [Fact]
    public async Task A_burst_of_change_signals_causes_one_reload_after_the_pause()
    {
        var (_, chat, rt, _) = await Open();
        var before = chat.ThreadCalls;
        for (var i = 0; i < 5; i++) rt.RaiseChanged();
        await Task.Delay(300);
        Assert.Equal(before + 1, chat.ThreadCalls);
    }

    [Fact]
    public async Task A_change_signal_brings_in_a_new_message_without_touching_the_draft()
    {
        var (gw, _, rt, member) = await Open();
        member.Draft = "half typed";
        var count = member.Bubbles.Count;

        var (_, _, _, lena) = await Open("coach@demo.io", gw);   // Lena, the demo coach, answers
        lena.Draft = "see you at 6"; await lena.SendCommand.ExecuteAsync(null);

        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        rt.RaiseChanged();
        await Task.Delay(300);
        Assert.Equal(count + 1, member.Bubbles.Count);
        Assert.Equal("see you at 6", member.Bubbles.Last().Line.Body);
        Assert.Equal("half typed", member.Draft);
    }

    [Fact]
    public async Task Leaving_the_page_stops_realtime_and_later_signals_do_nothing()
    {
        var (_, chat, rt, vm) = await Open();
        vm.OnDisappearing();
        Assert.Equal(1, rt.Stops);
        Assert.False(rt.HasListeners);
        var before = chat.ThreadCalls;
        rt.RaiseChanged();
        await Task.Delay(100);
        Assert.Equal(before, chat.ThreadCalls);
    }

    [Fact]
    public async Task Coming_back_as_someone_else_restarts_realtime_for_them()
    {
        var (gw, _, rt, vm) = await Open("member@demo.io");
        var first = gw.CurrentUserId!;
        vm.OnDisappearing();
        await gw.SignOutAsync(); await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        await vm.OnAppearingAsync();
        Assert.Equal(new[] { first, gw.CurrentUserId! }, rt.Started);
        Assert.NotEqual(first, gw.CurrentUserId);
    }

    [Fact]
    public async Task Without_a_realtime_service_the_page_works_as_before()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MessengerViewModel(new ChatService(gw, new NotificationService(gw)), new ProfileService(gw), new AuthService(gw), new DemoPhotoPicker());
        await vm.OnAppearingAsync();
        Assert.NotEmpty(vm.Threads);
        Assert.Equal(TimeSpan.FromSeconds(6), vm.RefreshEvery);
        vm.OnDisappearing();   // nothing to stop, and no error
    }

    [Fact]
    public async Task The_open_conversation_is_watched_and_switching_changes_the_watched_channel()
    {
        var (_, _, rt, vm) = await Open();
        Assert.Equal(vm.Selected!.Thread.Key, rt.Watched.Last());
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.OpenCommand.ExecuteAsync(vm.Threads.Single());
        Assert.Equal(vm.Selected!.Thread.Key, rt.Watched.Last());
        Assert.StartsWith("support|", rt.Watched.Last());
    }

    [Fact]
    public async Task Typing_from_the_other_side_shows_then_hides_by_itself()
    {
        var (_, _, rt, vm) = await Open();
        Assert.False(vm.OtherTyping);
        rt.RaiseTyping(vm.Selected!.Thread.Key);
        Assert.True(vm.OtherTyping);
        Assert.Equal("typing…", vm.StatusLine);
        await Task.Delay(250);   // TypingHold is 80 ms in these tests
        Assert.False(vm.OtherTyping);
    }

    [Fact]
    public async Task Typing_in_a_conversation_that_is_not_open_is_ignored()
    {
        var (_, _, rt, vm) = await Open();
        rt.RaiseTyping("coach|someone|else");
        Assert.False(vm.OtherTyping);
    }

    [Fact]
    public async Task A_message_from_them_ends_the_typing_line()
    {
        var (gw, _, rt, member) = await Open();
        member.TypingHold = TimeSpan.FromSeconds(30);
        rt.RaiseTyping(member.Selected!.Thread.Key);
        Assert.True(member.OtherTyping);

        var (_, _, _, lena) = await Open("coach@demo.io", gw);
        lena.Draft = "here"; await lena.SendCommand.ExecuteAsync(null);

        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        await member.AutoRefreshAsync();
        Assert.False(member.OtherTyping);
    }

    [Fact]
    public async Task Typing_is_sent_at_most_once_in_the_window_and_never_for_an_empty_box()
    {
        var (_, _, rt, vm) = await Open();
        vm.TypingEvery = TimeSpan.FromHours(1);
        vm.Draft = "h"; vm.Draft = "he"; vm.Draft = "hel";
        Assert.Equal(new[] { vm.Selected!.Thread.Key }, rt.TypingSent);
        vm.Draft = "";
        Assert.Single(rt.TypingSent);
    }

    [Fact]
    public async Task With_no_wait_every_keystroke_is_a_ping()
    {
        var (_, _, rt, vm) = await Open();
        vm.TypingEvery = TimeSpan.Zero;
        vm.Draft = "h"; vm.Draft = "he";
        Assert.Equal(2, rt.TypingSent.Count);
    }

    [Fact]
    public async Task Restoring_a_saved_draft_in_another_conversation_is_not_typing_and_clears_the_line()
    {
        var (_, _, rt, vm) = await Open("carlo@demo.io");
        vm.TypingEvery = TimeSpan.Zero;
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.OpenCommand.ExecuteAsync(vm.Threads.Single());
        vm.Draft = "my payment is stuck";
        var pings = rt.TypingSent.Count;
        rt.RaiseTyping(vm.Selected!.Thread.Key);
        Assert.True(vm.OtherTyping);

        await vm.ShowTabCommand.ExecuteAsync("coach");   // leave: the draft is kept, the line goes
        Assert.False(vm.OtherTyping);
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.OpenCommand.ExecuteAsync(vm.Threads.Single());   // back: the draft returns by itself
        Assert.Equal("my payment is stuck", vm.Draft);
        Assert.Equal(pings, rt.TypingSent.Count);
    }

    [Fact]
    public async Task A_conversation_you_cannot_write_in_sends_no_typing()
    {
        var (_, _, rt, vm) = await Open();
        vm.TypingEvery = TimeSpan.Zero;
        vm.Selected = new ThreadItem(new ChatThread { Kind = "coach", CoachId = "x", MemberId = "y", WithId = "x", CanSend = false });
        var before = rt.TypingSent.Count;
        vm.Draft = "can't";
        Assert.Equal(before, rt.TypingSent.Count);
    }

    [Fact]
    public async Task A_coach_who_is_online_gets_a_dot_in_the_list_and_Online_in_the_header()
    {
        var (_, _, rt, vm) = await Open();
        var lena = vm.Selected!.Thread.WithId!;
        Assert.False(vm.Threads.Single().IsOnline);
        rt.RaiseOnline(lena, "someone-else");
        Assert.True(vm.Threads.Single().IsOnline);
        Assert.Equal("Online", vm.StatusLine);
        rt.RaiseOnline();
        Assert.False(vm.Threads.Single().IsOnline);
        Assert.Equal("", vm.StatusLine);   // never seen: nothing is better than a guess
    }

    [Fact]
    public async Task When_they_are_not_online_the_header_says_when_they_were_last_active()
    {
        var (_, _, _, vm) = await Open();
        vm.Selected!.Thread.WithLastSeen = DateTime.UtcNow.AddHours(-3);
        Assert.Equal("Active 3 h ago", vm.StatusLine);   // read each time it is asked, so no event is needed in the test
    }

    [Fact]
    public async Task Typing_wins_over_online_in_the_header()
    {
        var (_, _, rt, vm) = await Open();
        rt.RaiseOnline(vm.Selected!.Thread.WithId!);
        rt.RaiseTyping(vm.Selected.Thread.Key);
        Assert.Equal("typing…", vm.StatusLine);
    }

    [Fact]
    public async Task A_members_own_front_desk_thread_never_shows_presence_even_if_its_id_is_online()
    {
        var (_, _, rt, vm) = await Open("carlo@demo.io");
        await vm.ShowTabCommand.ExecuteAsync("support");
        var desk = vm.Threads.Single();
        Assert.Null(desk.Thread.WithId);
        rt.RaiseOnline("", "u-emp", "u-admin");
        Assert.False(desk.IsOnline);
        await vm.OpenCommand.ExecuteAsync(desk);
        Assert.Equal("", vm.StatusLine);
    }

    [Fact]
    public async Task Front_desk_sees_a_member_as_online()
    {
        var gw = new DemoGateway();
        var (_, _, _, carlo) = await Open("carlo@demo.io", gw);
        await carlo.ShowTabCommand.ExecuteAsync("support");
        carlo.Draft = "hello"; await carlo.SendCommand.ExecuteAsync(null);

        var (_, _, rt, desk) = await Open("employee@demo.io", gw);
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        rt.RaiseOnline("u-carlo");
        Assert.True(desk.Threads.Single().IsOnline);
        Assert.Equal("Online", desk.StatusLine);
    }

    [Fact]
    public async Task Leaving_the_page_forgets_who_was_online()
    {
        var (_, _, rt, vm) = await Open();
        rt.RaiseOnline(vm.Selected!.Thread.WithId!);
        vm.OnDisappearing();
        Assert.False(vm.Threads.Single().IsOnline);
        Assert.Equal("", vm.StatusLine);
    }
}
