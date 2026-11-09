using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 4 (F9-F11, 0051) through the demo gateway that mirrors the database.
public class MessagingTests
{
    static async Task<(DemoGateway Gw, MessengerViewModel Vm, ConfirmationTests.Nav Nav)> Open(string email, DemoGateway? gw = null, bool coach = false)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var nav = new ConfirmationTests.Nav();
        var vm = new MessengerViewModel(new ChatService(gw, new NotificationService(gw)), new ProfileService(gw), new AuthService(gw), new DemoPhotoPicker(), nav: nav) { IsCoach = coach };
        await vm.OnAppearingAsync();
        return (gw, vm, nav);
    }

    static async Task Post(DemoGateway gw, string body, string audience = "everyone")
    {
        await gw.SignInAsync("subadmin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await new AnnouncementService(gw).PostAsync(body, audience)).Ok);
    }

    [Fact]
    public async Task Announcements_are_pinned_for_members_and_coaches_read_only_and_not_for_staff()
    {
        var gw = new DemoGateway();
        await Post(gw, "Closed on Monday for repairs");
        var (_, member, _) = await Open("member@demo.io", gw);
        Assert.NotNull(member.Pinned);
        Assert.Equal((1, "Closed on Monday for repairs"), (member.Pinned!.Thread.Unread, member.Pinned.Thread.LastBody));
        Assert.DoesNotContain(member.Threads, t => t.Thread.IsAnnouncements);   // above the tabs, not in them
        await member.OpenCommand.ExecuteAsync(member.Pinned);
        Assert.True(member.ReadOnly);
        Assert.StartsWith("Announcements are from the gym.", member.ReadOnlyText);
        Assert.Equal(("Closed on Monday for repairs", "Announcement"), (member.Bubbles.Single().Line.Body, member.Bubbles.Single().Line.SenderName));
        await member.AutoRefreshAsync();
        Assert.Equal(0, member.Pinned!.Thread.Unread);

        var (_, coach, _) = await Open("kai@demo.io", gw, coach: true);
        Assert.NotNull(coach.Pinned);
        var (_, desk, _) = await Open("employee@demo.io", gw);
        Assert.Null(desk.Pinned);
    }

    [Fact]
    public async Task A_members_only_announcement_skips_coaches()
    {
        var gw = new DemoGateway();
        await Post(gw, "Members' night on Friday", "members");
        Assert.Equal(1, (await Open("member@demo.io", gw)).Vm.Pinned!.Thread.Unread);
        Assert.Equal(0, (await Open("kai@demo.io", gw, coach: true)).Vm.Pinned!.Thread.Unread);
    }

    [Fact]
    public async Task Writing_to_Admin_starts_that_conversation_and_only_Admin_and_System_admin_see_it()
    {
        var (gw, bea, _) = await Open("bea@demo.io");
        await bea.WriteToCommand.ExecuteAsync(ChatQueue.Admin);
        Assert.Equal(("Admin", ChatQueue.Admin, "support"), (bea.Selected!.Thread.WithName, bea.WritingTo, bea.Tab));
        Assert.Empty(bea.Bubbles);
        bea.Draft = "My card was charged twice"; await bea.SendCommand.ExecuteAsync(null);
        Assert.Contains(bea.Threads, t => t.Thread.Queue == ChatQueue.Admin && t.Thread.LastBody == "My card was charged twice");

        foreach (var who in new[] { "subadmin@demo.io", "admin@demo.io" })
        {
            var (_, staff, _) = await Open(who, gw);
            var row = staff.Threads.Single(t => t.Thread.Queue == ChatQueue.Admin);
            Assert.Equal(("Bea Lim", "To Admin"), (row.Thread.WithName, row.QueueTag));
        }
        var (_, desk, _) = await Open("employee@demo.io", gw);
        Assert.DoesNotContain(desk.Threads, t => t.Thread.Queue == ChatQueue.Admin);
    }

    [Fact]
    public async Task A_coach_reaches_the_System_admin_and_nobody_else_reads_it()
    {
        var (gw, kai, _) = await Open("kai@demo.io", coach: true);
        Assert.True(kai.HasTabs);
        Assert.Equal("Gym", kai.StaffTabText);
        await kai.WriteToCommand.ExecuteAsync(ChatQueue.SystemAdmin);
        kai.Draft = "Shift swap next week"; await kai.SendCommand.ExecuteAsync(null);
        Assert.Contains((await Open("admin@demo.io", gw)).Vm.Threads, t => t.Thread.Queue == ChatQueue.SystemAdmin && t.QueueTag == "To System admin");
        Assert.DoesNotContain((await Open("subadmin@demo.io", gw)).Vm.Threads, t => t.Thread.Queue == ChatQueue.SystemAdmin);
        var chat = new ChatService(gw, new NotificationService(gw));
        Assert.Equal(AppErrorKind.Unauthorized, (await chat.MessagesAsync(new ChatThread { Kind = "support", MemberId = "u-coach2", Queue = ChatQueue.SystemAdmin })).Error);
    }

    [Fact]
    public async Task A_photo_to_Admin_goes_in_that_queues_folder()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var chat = new ChatService(gw, new NotificationService(gw));
        Assert.True((await chat.SendPhotoAsync(ChatThread.ForQueue("u-bea", ChatQueue.Admin), "receipt.png", DemoPng.Make(), "")).Ok);
        Assert.StartsWith("support/admin/u-bea/", (await gw.ListAsync<SupportMessage>(m => m.Queue == ChatQueue.Admin)).Single().AttachmentPath);
    }

    [Fact]
    public async Task A_coach_invitation_and_the_members_answer_show_in_the_chat_as_request_cards_in_each_ones_name()
    {
        var gw = new DemoGateway();
        var svc = new SessionService(gw);
        var start = DateTime.UtcNow.Date.AddDays(14).AddHours(9);
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        foreach (var row in (await svc.SessionsAsync()).Value!.Where(r => r.Session.MemberId == "u-alex" && r.Session.Status == Status.Scheduled))
            Assert.True((await svc.CancelSessionAsync(row.Session.Id)).Ok);
        Assert.True((await svc.InviteMemberAsync("u-alex", start, start.AddHours(1))).Ok);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var invite = (await svc.RequestsAsync()).Value!.Rows.Where(r => r.Request.RequestedBy == "coach" && r.Request.Status == "pending").MaxBy(r => r.Request.Id)!;
        Assert.True((await svc.DeclineAsync(invite.Request.Id, "Away that week")).Ok);

        var (_, alex, nav) = await Open("member@demo.io", gw);
        await alex.OpenCommand.ExecuteAsync(alex.Threads.Single(t => t.Thread.CoachId == "u-coach1"));
        var cards = alex.Bubbles.Where(b => b.IsRequest).TakeLast(2).ToList();
        Assert.Equal(("Training session invitation", false), (cards[0].RequestTitle, cards[0].Mine));
        Assert.Equal(("Training session declined", true), (cards[1].RequestTitle, cards[1].Mine));
        Assert.EndsWith("Reason: Away that week", cards[1].Line.Body);
        Assert.Equal(invite.Request.Id, cards[1].Line.TimeRequestId);
        alex.OpenRequestsCommand.Execute(null);
        Assert.Equal(1, nav.Requests);
    }

    [Fact]
    public async Task The_Announcements_page_posts_to_the_chosen_audience_and_lists_what_was_posted()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("subadmin@demo.io", DemoGateway.DemoPassword);
        var vm = new AnnouncementsViewModel(new AnnouncementService(gw));
        await vm.OnAppearingAsync();
        Assert.False(vm.PostCommand.CanExecute(null));
        vm.Body = "  New yoga class on Thursdays  "; vm.PickAudienceCommand.Execute("members");
        Assert.Equal("27 / 2000", vm.CountText);
        await vm.PostCommand.ExecuteAsync(null);
        Assert.Equal("Posted to all members.", vm.Notice);
        Assert.Equal(("", "everyone"), (vm.Body, vm.Audience));
        var row = vm.Posted.Single();
        Assert.Equal(("New yoga class on Thursdays", "Members", "Sam Villanueva"), (row.Body, row.AudienceText, row.AuthorName));

        foreach (var who in new[] { "employee@demo.io", "coach@demo.io", "member@demo.io" })
        {
            await gw.SignInAsync(who, DemoGateway.DemoPassword);
            Assert.Equal(AppErrorKind.Unauthorized, (await new AnnouncementService(gw).PostAsync("Hi", "everyone")).Error);
        }
    }
}
