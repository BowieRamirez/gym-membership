using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

// 0132 (owner, 2026-11-21): an archived account's chats leave the Messages list; restoring brings them back.
public class ChatArchivedTests
{
    [Fact]
    public async Task An_archived_members_chat_leaves_the_desk_inbox_and_comes_back_on_restore()
    {
        var gw = new DemoGateway();
        var chat = new ChatService(gw, new NotificationService(gw));
        await gw.SignInAsync("carlo@demo.io", DemoGateway.DemoPassword);
        var own = (await chat.ThreadsAsync()).Value!.First(t => t.Kind == "support");
        Assert.True((await chat.SendAsync(own, "Hello desk")).Ok);

        async Task<bool> DeskSeesCarlo()
        {
            await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
            return (await chat.ThreadsAsync()).Value!.Any(t => t.Kind == "support" && t.MemberId == "u-carlo");
        }
        Assert.True(await DeskSeesCarlo());

        var archive = new AccountArchiveService(gw);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await archive.DeleteAsync("u-carlo", "Test account")).Ok);
        Assert.False(await DeskSeesCarlo());

        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await archive.RestoreAsync("u-carlo")).Ok);
        Assert.True(await DeskSeesCarlo());
    }
}
