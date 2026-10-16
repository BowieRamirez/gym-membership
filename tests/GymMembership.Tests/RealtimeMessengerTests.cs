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
        { ChangeDelay = TimeSpan.FromMilliseconds(20) };
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
}
