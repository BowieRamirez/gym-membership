using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class ChatServiceTests
{
    static async Task<(DemoGateway Gw, ChatService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new ChatService(gw, new NotificationService(gw)));
    }

    [Fact]
    public async Task A_member_sees_the_coach_chat_with_its_last_message_and_a_front_desk_thread()
    {
        var (_, svc) = await As("member@demo.io");
        var threads = (await svc.ThreadsAsync()).Value!;

        var coach = threads.Single(t => !t.IsSupport);
        Assert.Equal("Lena Cruz", coach.WithName);
        Assert.Equal("Perfect, see you on the session.", coach.LastBody);
        Assert.True(coach.LastFromMe);
        Assert.True(coach.CanSend);
        var desk = threads.Single(t => t.IsSupport);
        Assert.Equal("Front desk", desk.WithName);
        Assert.Null(desk.LastBody);   // nothing written yet, but the thread is always there
    }

    [Fact]
    public async Task Messages_come_back_oldest_first_with_a_status_on_the_ones_you_sent()
    {
        var (_, svc) = await As("member@demo.io");
        var coach = (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport);

        var page = (await svc.MessagesAsync(coach)).Value!;

        Assert.False(page.HasMore);
        Assert.Equal(page.Bubbles.OrderBy(b => b.Line.SentAt).Select(b => b.Line.MessageId), page.Bubbles.Select(b => b.Line.MessageId));
        var mine = page.Bubbles.First(b => b.Mine);
        Assert.Contains(mine.Status, new[] { "Sent", "Delivered", "Read" });
        Assert.Equal("", page.Bubbles.First(b => !b.Mine).Status);   // status is shown on what you sent, not what you received
    }

    [Fact]
    public async Task A_long_conversation_loads_a_page_at_a_time_without_duplicates()
    {
        var (gw, svc) = await As("member@demo.io");
        var coach = (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport);
        for (var i = 0; i < 120; i++) await svc.SendAsync(coach, $"message {i}");

        var first = (await svc.MessagesAsync(coach)).Value!;
        Assert.Equal(50, first.Bubbles.Count);
        Assert.True(first.HasMore);
        Assert.Equal("message 119", first.Bubbles[^1].Line.Body);   // newest at the bottom

        var second = (await svc.MessagesAsync(coach, first.Bubbles[0].Line.MessageId)).Value!;
        Assert.Equal(50, second.Bubbles.Count);
        Assert.True(second.HasMore);
        var third = (await svc.MessagesAsync(coach, second.Bubbles[0].Line.MessageId)).Value!;
        Assert.False(third.HasMore);   // 3 seeded + 120 sent = 123; the last page is the 23 oldest
        Assert.Equal(123, first.Bubbles.Count + second.Bubbles.Count + third.Bubbles.Count);
        Assert.Equal(123, first.Bubbles.Concat(second.Bubbles).Concat(third.Bubbles).Select(b => b.Line.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Sending_to_a_coach_needs_text_and_an_active_hire_and_never_adds_a_phantom_message_when_it_fails()
    {
        var (gw, svc) = await As("member@demo.io");
        var coach = (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport);
        Assert.Equal("empty_message", (await svc.SendAsync(coach, "   ")).Message);
        Assert.Equal("message_too_long", (await svc.SendAsync(coach, new string('x', 2001))).Message);

        gw.FailWith = new HttpRequestException("offline");
        Assert.Equal(AppErrorKind.Offline, (await svc.SendAsync(coach, "hello")).Error);
        gw.FailWith = null;
        Assert.DoesNotContain((await svc.MessagesAsync(coach)).Value!.Bubbles, b => b.Line.Body == "hello");
    }

    [Fact]
    public async Task Reading_a_conversation_shows_Read_to_the_sender_and_clears_the_unread_badge()
    {
        var (gw, alex) = await As("member@demo.io");
        var toCoach = (await alex.ThreadsAsync()).Value!.Single(t => !t.IsSupport);
        await alex.SendAsync(toCoach, "see you at 9");

        var (_, coach) = await As("coach@demo.io", gw);
        var theirs = (await coach.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan");
        Assert.True(theirs.Unread >= 1);
        await coach.MarkReadAsync(theirs);
        Assert.Equal(0, (await coach.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan").Unread);

        var (_, alexAgain) = await As("member@demo.io", gw);
        var sent = (await alexAgain.MessagesAsync((await alexAgain.ThreadsAsync()).Value!.Single(t => !t.IsSupport))).Value!.Bubbles.Last(b => b.Mine);
        Assert.Equal("Read", sent.Status);
    }

    [Fact]
    public async Task Loading_the_list_marks_what_you_received_as_delivered_but_not_read()
    {
        var (gw, coach) = await As("coach@demo.io");
        var toAlex = (await coach.ThreadsAsync()).Value!.Single(t => t.WithName == "Alex Tan");
        await coach.SendAsync(toAlex, "bring shoes");

        var (_, alex) = await As("member@demo.io", gw);
        await alex.MarkDeliveredAsync();

        var (_, coachAgain) = await As("coach@demo.io", gw);
        var line = (await coachAgain.MessagesAsync(toAlex)).Value!.Bubbles.Last(b => b.Mine);
        Assert.Equal("Delivered", line.Status);
    }

    [Fact]
    public async Task A_member_writes_to_the_front_desk_staff_see_it_in_the_inbox_and_answer_and_the_member_never_learns_who()
    {
        var (gw, bea) = await As("bea@demo.io");
        var deskThread = (await bea.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.True((await bea.SendAsync(deskThread, "my payment is stuck")).Ok);

        var (_, staff) = await As("employee@demo.io", gw);
        var inbox = (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport && t.WithName == "Bea Lim");
        Assert.Equal(1, inbox.Unread);
        Assert.Equal("open", inbox.Status);
        Assert.True((await staff.SendAsync(inbox, "checking now")).Ok);
        var asStaff = (await staff.MessagesAsync(inbox)).Value!.Bubbles;
        Assert.Equal("Marco Santos", asStaff.Last().Line.SenderName);   // staff see who answered

        var (_, beaAgain) = await As("bea@demo.io", gw);
        var asMember = (await beaAgain.MessagesAsync((await beaAgain.ThreadsAsync()).Value!.Single(t => t.IsSupport))).Value!.Bubbles;
        Assert.Equal("checking now", asMember.Last().Line.Body);
        Assert.Null(asMember.Last().Line.SenderName);                    // a member sees "Front desk" only
    }

    [Fact]
    public async Task Staff_take_close_and_reopen_a_conversation_and_a_member_cannot()
    {
        var (gw, bea) = await As("bea@demo.io");
        await bea.SendAsync((await bea.ThreadsAsync()).Value!.Single(t => t.IsSupport), "hello");
        var (_, staff) = await As("employee@demo.io", gw);
        var inbox = (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport);

        Assert.True((await staff.ClaimAsync(inbox, true)).Ok);
        var taken = (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.Equal("Marco Santos", taken.ClaimedByName);

        var (_, admin) = await As("admin@demo.io", gw);
        Assert.Equal(AppErrorKind.Conflict, (await admin.ClaimAsync(inbox, true)).Error);   // already taken
        Assert.True((await admin.ClaimAsync(inbox, false)).Ok);                                // an admin may release

        Assert.True((await staff.SetOpenAsync(inbox, false)).Ok);
        Assert.Equal("closed", (await staff.ThreadsAsync()).Value!.Single(t => t.IsSupport).Status);

        var (_, member) = await As("bea@demo.io", gw);
        var mine = (await member.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.Equal(AppErrorKind.Unauthorized, (await member.SetOpenAsync(mine, false)).Error);
    }

    [Fact]
    public async Task Search_finds_people_and_message_text_among_your_own_conversations_only()
    {
        var (_, coach) = await As("coach@demo.io");
        Assert.Contains((await coach.SearchAsync("alex")).Value!, h => h.WithName == "Alex Tan");
        var byText = (await coach.SearchAsync("flat-soled")).Value!;
        Assert.Single(byText);
        Assert.Contains("flat-soled", byText[0].Snippet);
        Assert.Empty((await coach.SearchAsync("a")).Value!);        // under two characters
        Assert.Empty((await coach.SearchAsync("%")).Value!);

        var (_, bea) = await As("bea@demo.io");
        Assert.Empty((await bea.SearchAsync("flat-soled")).Value!); // not Bea's conversation
    }

    [Fact]
    public async Task Opening_from_Coaches_or_Trainees_selects_that_conversation_first()
    {
        var (_, svc) = await As("member@demo.io");
        svc.Pending = ("u-coach1", null);   // "me, as the member"
        Assert.Equal("u-coach1", svc.Pending!.Value.CoachId);
    }
}
