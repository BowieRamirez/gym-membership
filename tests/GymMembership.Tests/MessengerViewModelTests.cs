using System.Collections.Specialized;
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MessengerViewModelTests
{
    static async Task<(DemoGateway Gw, ChatService Chat, MessengerViewModel Vm)> Open(string email, DemoGateway? gw = null, bool compact = false)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw));
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw)) { IsCompact = compact };
        await vm.OnAppearingAsync();
        return (gw, chat, vm);
    }

    [Fact]
    public async Task A_member_opens_on_the_coach_tab_with_the_first_conversation_open_and_marked_read()
    {
        var (_, _, vm) = await Open("member@demo.io");
        Assert.True(vm.HasTabs);
        Assert.Equal("coach", vm.Tab);
        Assert.Equal("Lena Cruz", vm.Selected!.Thread.WithName);
        Assert.NotEmpty(vm.Bubbles);
        Assert.Equal(0, vm.Threads.Single().Thread.Unread);   // opening it read it
    }

    [Fact]
    public async Task The_staff_tab_shows_the_front_desk_thread_and_staff_find_it_in_their_inbox()
    {
        var (gw, _, vm) = await Open("carlo@demo.io");
        Assert.Empty(vm.Threads);                                   // Carlo has no coach: the Coach tab is empty
        Assert.Contains("coach", vm.EmptyListText, StringComparison.OrdinalIgnoreCase);
        await vm.ShowTabCommand.ExecuteAsync("support");
        Assert.Equal("Front desk", vm.Threads.Single().Thread.WithName);

        vm.Draft = "my payment is stuck";
        await vm.SendCommand.ExecuteAsync(null);
        Assert.Equal("", vm.Draft);
        Assert.Equal("my payment is stuck", vm.Bubbles.Last().Line.Body);
        Assert.Equal("Sent", vm.Bubbles.Last().Status);

        var (_, _, desk) = await Open("employee@demo.io", gw);
        Assert.False(desk.HasTabs);
        Assert.True(desk.HasFilters);
        Assert.Equal("Carlo Diaz", desk.Threads.Single().Thread.WithName);
    }

    [Fact]
    public async Task Staff_take_close_and_reopen_from_the_chat_header()
    {
        var (gw, _, bea) = await Open("bea@demo.io");
        await bea.ShowTabCommand.ExecuteAsync("support");
        bea.Draft = "hello"; await bea.SendCommand.ExecuteAsync(null);

        var (_, _, desk) = await Open("employee@demo.io", gw);
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        Assert.True(desk.CanTake); Assert.False(desk.CanRelease);

        await desk.TakeCommand.ExecuteAsync(null);
        Assert.True(desk.CanRelease); Assert.False(desk.CanTake);
        Assert.Equal("open", desk.Filter);   // the filter does not jump by itself; a taken chat shows under Mine (checked next)
        await desk.ShowFilterCommand.ExecuteAsync("mine");
        Assert.Single(desk.Threads);

        await desk.CloseChatCommand.ExecuteAsync(null);
        await desk.ShowFilterCommand.ExecuteAsync("closed");
        Assert.Single(desk.Threads);
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        Assert.True(desk.CanReopen);
    }

    // a chat service whose message pages the test releases by hand, to make two conversations answer out of order
    sealed class SlowChat : IChatService
    {
        public (string CoachId, string? MemberId)? Pending { get; set; }
        public readonly Queue<(ChatThread Thread, TaskCompletionSource<AppResult<ChatPage>> Reply)> Waiting = new();
        public List<ChatThread> Threads = [];
        public Task<AppResult<IReadOnlyList<ChatThread>>> ThreadsAsync() => Task.FromResult(AppResult<IReadOnlyList<ChatThread>>.Success(Threads));
        public Task<AppResult<ChatPage>> MessagesAsync(ChatThread t, long? beforeId = null, int limit = 50)
        { var tcs = new TaskCompletionSource<AppResult<ChatPage>>(); Waiting.Enqueue((t, tcs)); return tcs.Task; }
        public Task<AppResult<bool>> SendAsync(ChatThread t, string body) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> MarkReadAsync(ChatThread t) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> MarkDeliveredAsync() => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> ClaimAsync(ChatThread t, bool claim) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<bool>> SetOpenAsync(ChatThread t, bool open) => Task.FromResult(AppResult<bool>.Success(true));
        public Task<AppResult<IReadOnlyList<ChatSearchHit>>> SearchAsync(string q) => Task.FromResult(AppResult<IReadOnlyList<ChatSearchHit>>.Success([]));
    }

    static ChatPage PageOf(string body) => new([new ChatBubble(new ChatLine { MessageId = 1, SenderId = "x", Body = body, SentAt = DateTime.UtcNow }, false)], false);

    [Fact]
    public async Task A_slow_answer_for_the_first_conversation_never_shows_under_the_second()
    {
        var chat = new SlowChat();
        chat.Threads = [new ChatThread { Kind = "coach", CoachId = "c-1", MemberId = "m-1", WithName = "Anna", CanSend = true },
                        new ChatThread { Kind = "coach", CoachId = "c-2", MemberId = "m-1", WithName = "Ben", CanSend = true }];
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw));
        var loading = vm.OnAppearingAsync();
        await Task.Delay(50);
        var first = chat.Waiting.Dequeue();          // Anna's messages are being fetched
        var anna = vm.Threads.First(t => t.Thread.WithName == "Anna"); var ben = vm.Threads.First(t => t.Thread.WithName == "Ben");

        var openBen = vm.OpenCommand.ExecuteAsync(ben);   // the person switches before Anna's answer arrives
        await Task.Delay(50);
        var second = chat.Waiting.Dequeue();
        second.Reply.SetResult(AppResult<ChatPage>.Success(PageOf("Ben's message")));
        await openBen;
        first.Reply.SetResult(AppResult<ChatPage>.Success(PageOf("Anna's message")));   // the stale answer lands last
        await loading;
        await Task.Delay(50);

        Assert.Equal("Ben", vm.Selected!.Thread.WithName);
        Assert.Equal(["Ben's message"], vm.Bubbles.Select(b => b.Line.Body));
    }

    [Fact]
    public async Task Earlier_messages_are_added_above_without_duplicates_and_the_button_goes_away_at_the_start()
    {
        var (_, chat, vm) = await Open("member@demo.io");
        for (var i = 0; i < 70; i++) { vm.Draft = $"m{i}"; await vm.SendCommand.ExecuteAsync(null); }
        await vm.OnAppearingAsync();   // reload: only the newest page
        Assert.Equal(50, vm.Bubbles.Count);
        Assert.True(vm.HasMore);

        await vm.LoadEarlierCommand.ExecuteAsync(null);

        Assert.Equal(73, vm.Bubbles.Count);   // 3 seeded + 70 sent
        Assert.False(vm.HasMore);
        Assert.Equal(73, vm.Bubbles.Select(b => b.Line.MessageId).Distinct().Count());
        Assert.Equal("m69", vm.Bubbles.Last().Line.Body);
    }

    [Fact]
    public async Task Offline_the_draft_stays_with_an_error_line_and_no_phantom_bubble_appears()
    {
        var (gw, _, vm) = await Open("member@demo.io");
        var before = vm.Bubbles.Count;
        vm.Draft = "are we still on?";
        gw.FailWith = new HttpRequestException("offline");

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("are we still on?", vm.Draft);
        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.Equal(before, vm.Bubbles.Count);
    }

    [Fact]
    public async Task The_automatic_refresh_adds_new_messages_keeps_the_draft_and_the_error_and_stays_silent_offline()
    {
        var (gw, _, member) = await Open("member@demo.io");
        member.Draft = "typing...";
        member.ErrorMessage = "an error being read";

        var (_, coachChat, _) = await Open("coach@demo.io", gw);   // Lena answers on another screen
        await coachChat.SendAsync((await coachChat.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan"), "see you at 9");
        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);

        var actions = new List<NotifyCollectionChangedAction>();
        member.Bubbles.CollectionChanged += (_, e) => actions.Add(e.Action);
        await member.AutoRefreshAsync();

        Assert.Equal("see you at 9", member.Bubbles.Last().Line.Body);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);   // the chat was not rebuilt
        Assert.Equal("typing...", member.Draft);
        Assert.Equal("an error being read", member.ErrorMessage);

        gw.FailWith = new HttpRequestException("offline");
        await member.AutoRefreshAsync();
        Assert.Equal("an error being read", member.ErrorMessage);   // no new error while offline
    }

    [Fact]
    public async Task On_a_phone_the_list_comes_first_then_the_chat_and_Back_returns()
    {
        var (_, _, vm) = await Open("member@demo.io", compact: true);
        Assert.True(vm.ShowList); Assert.False(vm.ShowChat);
        await vm.OpenCommand.ExecuteAsync(vm.Threads.First());
        Assert.False(vm.ShowList); Assert.True(vm.ShowChat);
        vm.BackCommand.Execute(null);
        Assert.True(vm.ShowList); Assert.False(vm.ShowChat);

        var (_, _, desktop) = await Open("member@demo.io", compact: false);
        Assert.True(desktop.ShowList); Assert.True(desktop.ShowChat);   // both panes at once
    }

    [Fact]
    public async Task Opening_from_Coaches_selects_that_conversation()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw)) { Pending = ("u-coach1", null) };
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw));
        await vm.OnAppearingAsync();
        Assert.Equal("Lena Cruz", vm.Selected!.Thread.WithName);
        Assert.Null(chat.Pending);   // used once
    }

    [Fact]
    public async Task Searching_filters_the_list_by_name_at_once_and_adds_message_matches()
    {
        var (_, _, vm) = await Open("coach@demo.io");
        vm.SearchText = "alex";
        await Task.Delay(700);   // the server search waits for a short pause in typing
        Assert.True(vm.IsSearching);
        Assert.All(vm.Threads, t => Assert.Contains("alex", t.Thread.WithName, StringComparison.OrdinalIgnoreCase));

        vm.SearchText = "flat-soled";
        await Task.Delay(700);
        Assert.Contains("flat-soled", vm.Hits.Single().Snippet);

        vm.SearchText = "";
        await Task.Delay(100);
        Assert.False(vm.IsSearching);
        Assert.Empty(vm.Hits);
    }
    [Fact]
    public async Task The_screen_has_words_for_the_open_row_the_unread_tab_the_read_only_chat_and_who_took_a_conversation()
    {
        var (gw, _, bea) = await Open("bea@demo.io");   // Bea has Lena as a coach but no active plan: she can read, not write
        Assert.True(bea.ReadOnly);
        Assert.True(bea.Selected!.IsOpen);

        await bea.ShowTabCommand.ExecuteAsync("support");
        bea.Draft = "hello"; await bea.SendCommand.ExecuteAsync(null);
        Assert.False(bea.ReadOnly);

        var (_, _, desk) = await Open("employee@demo.io", gw);
        Assert.Equal("Not taken yet", desk.Threads.Single().Subtitle);
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        await desk.TakeCommand.ExecuteAsync(null);
        Assert.Equal("Taken by Marco Santos", desk.Selected!.Subtitle);

        Assert.Equal("Coach", bea.CoachTabText);   // nothing unread on the coach side: no count
        desk.Draft = "on it"; await desk.SendCommand.ExecuteAsync(null);
        var (_, _, beaLater) = await Open("bea@demo.io", gw);
        Assert.Equal("Staff (1)", beaLater.StaffTabText);
    }
    // ---- fixes from the whole-branch review

    [Fact]
    public async Task A_half_typed_message_stays_with_its_own_conversation_and_never_goes_to_another()
    {
        var (_, _, vm) = await Open("member@demo.io");
        vm.Draft = "see you at 5";                              // meant for the coach

        await vm.ShowTabCommand.ExecuteAsync("support");         // the person moves to the front desk
        Assert.Equal("", vm.Draft);
        vm.Draft = "about my payment";

        await vm.ShowTabCommand.ExecuteAsync("coach");           // and back
        Assert.Equal("see you at 5", vm.Draft);
        await vm.ShowTabCommand.ExecuteAsync("support");
        Assert.Equal("about my payment", vm.Draft);
    }

    [Fact]
    public async Task Tapping_a_message_hit_in_a_conversation_the_filter_hides_still_opens_it()
    {
        var (gw, _, bea) = await Open("bea@demo.io");
        await bea.ShowTabCommand.ExecuteAsync("support");
        bea.Draft = "refund please"; await bea.SendCommand.ExecuteAsync(null);
        var (_, _, closing) = await Open("employee@demo.io", gw);
        await closing.OpenCommand.ExecuteAsync(closing.Threads.Single());
        await closing.CloseChatCommand.ExecuteAsync(null);       // now it is closed, and the desk's list shows Open

        var (_, _, desk) = await Open("employee@demo.io", gw);
        Assert.Empty(desk.Threads);
        desk.SearchText = "refund";
        await Task.Delay(700);
        await desk.OpenHitCommand.ExecuteAsync(desk.Hits.Single());

        Assert.Equal("Bea Lim", desk.Selected!.Thread.WithName);
        Assert.Equal("closed", desk.Filter);
        Assert.NotEmpty(desk.Bubbles);
    }

    [Fact]
    public async Task A_slow_answer_for_the_last_person_never_shows_under_the_next_one_who_signs_in()
    {
        var chat = new SlowChat { Threads = [new ChatThread { Kind = "coach", CoachId = "c-1", MemberId = "m-1", WithName = "Anna", CanSend = true }] };
        var gw = new DemoGateway(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new MessengerViewModel(chat, new ProfileService(gw), new AuthService(gw));
        var loading = vm.OnAppearingAsync();
        await Task.Delay(50);
        var stale = chat.Waiting.Dequeue();                      // the first person's messages are still on their way

        await gw.SignOutAsync(); await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword);
        chat.Threads = [];
        await vm.OnAppearingAsync();                             // the next person opens Messages
        stale.Reply.SetResult(AppResult<ChatPage>.Success(PageOf("the first person's message")));
        await loading;
        await Task.Delay(50);

        Assert.Empty(vm.Bubbles);
        Assert.Null(vm.Selected);
    }
}
