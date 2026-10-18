using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class ChatPhotoTests
{
    static readonly byte[] Photo = [1, 2, 3, 4];

    static async Task<(DemoGateway Gw, ChatService Svc)> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        if (gw.CurrentUserId is not null) await gw.SignOutAsync();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new ChatService(gw, new NotificationService(gw)));
    }

    static async Task<ChatThread> CoachThread(ChatService svc, string with = "Lena Cruz") => (await svc.ThreadsAsync()).Value!.Single(t => !t.IsSupport && t.WithName == with);
    static async Task<ChatThread> DeskThread(ChatService svc) => (await svc.ThreadsAsync()).Value!.Single(t => t.IsSupport);

    [Fact]
    public async Task A_member_sends_a_photo_to_the_coach_with_a_caption_and_both_see_it()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);

        Assert.True((await alex.SendPhotoAsync(thread, "Squat form.JPG", Photo, "  how is my depth?  ")).Ok);

        var mine = (await alex.MessagesAsync(thread)).Value!.Bubbles.Last();
        Assert.Equal("how is my depth?", mine.Line.Body);
        Assert.StartsWith("u-coach1/u-alex/", mine.Line.AttachmentPath);
        Assert.EndsWith("-Squat_form.JPG", mine.Line.AttachmentPath);
        Assert.True(mine.ShowPhoto); Assert.False(mine.PhotoMissing); Assert.True(mine.HasText);

        var (_, lena) = await As("coach@demo.io", gw);
        var theirs = (await lena.MessagesAsync(await CoachThread(lena, "Alex Tan"))).Value!.Bubbles.Last();
        Assert.True(theirs.ShowPhoto);
        Assert.False(theirs.Mine);
    }

    [Fact]
    public async Task A_photo_with_no_caption_is_a_message_and_the_list_says_Photo()
    {
        var (_, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);

        Assert.True((await alex.SendPhotoAsync(thread, "a.png", Photo, "")).Ok);

        var last = (await alex.MessagesAsync(thread)).Value!.Bubbles.Last();
        Assert.False(last.HasText); Assert.True(last.ShowPhoto);
        Assert.Equal("You: Photo", (await CoachThread(alex)).Preview);
        Assert.Equal(AppErrorKind.Validation, (await alex.SendAsync(thread, "  ")).Error);   // plain text still needs text
    }

    [Fact]
    public async Task A_wrong_file_type_an_empty_file_an_oversize_file_and_a_long_caption_are_refused_before_anything_is_uploaded()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);
        var before = (await alex.MessagesAsync(thread)).Value!.Bubbles.Count;

        foreach (var (name, data) in new (string, byte[])[] { ("notes.pdf", Photo), ("a.gif", Photo), ("a.jpg", []), ("a.png", new byte[ProfileService.MaxBytes + 1]), ("noextension", Photo) })
        {
            var r = await alex.SendPhotoAsync(thread, name, data, "");
            Assert.Equal(AppErrorKind.Validation, r.Error); Assert.Equal("bad_image", r.Message);
        }
        Assert.Equal("message_too_long", (await alex.SendPhotoAsync(thread, "a.png", Photo, new string('x', 2001))).Message);
        Assert.Equal(before, (await alex.MessagesAsync(thread)).Value!.Bubbles.Count);
    }

    [Fact]
    public async Task Nobody_outside_the_conversation_can_read_or_add_a_coach_chat_photo_not_even_staff()
    {
        var (gw, alex) = await As("member@demo.io");
        await alex.SendPhotoAsync(await CoachThread(alex), "a.png", Photo, "");
        var path = (await alex.MessagesAsync(await CoachThread(alex))).Value!.Bubbles.Last().Line.AttachmentPath!;

        foreach (var who in new[] { "carlo@demo.io", "employee@demo.io", "admin@demo.io", "bea@demo.io" })
        {
            await As(who, gw);
            Assert.Null(await gw.SignedUrlAsync("chat-attachments", path));
            await Assert.ThrowsAnyAsync<Exception>(() => gw.UploadAsync("chat-attachments", "u-coach1/u-alex/sneaky.png", Photo));
        }
        await As("coach@demo.io", gw);
        Assert.NotNull(await gw.SignedUrlAsync("chat-attachments", path));
    }

    [Fact]
    public async Task A_guest_and_an_ended_hire_can_read_old_photos_but_not_send_new_ones()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);
        await alex.SendPhotoAsync(thread, "a.png", Photo, "before");
        Assert.True((await new CoachService(gw).EndHireAsync((await gw.ListAsync<CoachHire>()).First(h => h.Status == "active").Id)).Ok);

        Assert.True((await alex.MessagesAsync(thread)).Value!.Bubbles.Last().ShowPhoto);                      // history stays
        Assert.Equal(AppErrorKind.Unauthorized, (await alex.SendPhotoAsync(thread, "b.png", Photo, "after")).Error);

        var (_, bea) = await As("bea@demo.io", gw);   // hired Lena but has no active plan
        Assert.Equal(AppErrorKind.Unauthorized, (await bea.SendPhotoAsync(await CoachThread(bea), "c.png", Photo, "")).Error);
    }

    [Fact]
    public async Task Front_desk_photos_go_both_ways_and_only_the_member_and_staff_see_them()
    {
        var (gw, bea) = await As("bea@demo.io");   // a guest may still write to the front desk
        Assert.True((await bea.SendPhotoAsync(await DeskThread(bea), "slip.png", Photo, "my payment slip")).Ok);
        var path = (await bea.MessagesAsync(await DeskThread(bea))).Value!.Bubbles.Last().Line.AttachmentPath!;
        Assert.StartsWith("support/u-bea/", path);

        var (_, marco) = await As("employee@demo.io", gw);
        var inbox = (await marco.ThreadsAsync()).Value!.Single(t => t.IsSupport);
        Assert.Equal("my payment slip", inbox.LastBody);   // a caption wins over the word Photo
        Assert.True((await marco.MessagesAsync(inbox)).Value!.Bubbles.Last().ShowPhoto);
        Assert.True((await marco.SendPhotoAsync(inbox, "receipt.png", Photo, "")).Ok);

        var (_, beaAgain) = await As("bea@demo.io", gw);
        Assert.True((await beaAgain.MessagesAsync(await DeskThread(beaAgain))).Value!.Bubbles.Last().ShowPhoto);

        foreach (var who in new[] { "member@demo.io", "coach@demo.io", "carlo@demo.io" })
        {
            await As(who, gw);
            Assert.Null(await gw.SignedUrlAsync("chat-attachments", path));
        }
    }

    [Fact]
    public async Task A_failed_send_adds_no_message_and_a_missing_link_shows_as_unavailable_not_blank()
    {
        var (gw, alex) = await As("member@demo.io");
        var thread = await CoachThread(alex);
        var before = (await alex.MessagesAsync(thread)).Value!.Bubbles.Count;

        gw.FailWith = new HttpRequestException("offline");
        Assert.Equal(AppErrorKind.Offline, (await alex.SendPhotoAsync(thread, "a.png", Photo, "hi")).Error);
        gw.FailWith = null;
        Assert.Equal(before, (await alex.MessagesAsync(thread)).Value!.Bubbles.Count);

        var gone = new ChatBubble(new ChatLine { AttachmentPath = "u-coach1/u-alex/x.png" }, true, null);   // a photo was sent but no link could be made
        Assert.True(gone.PhotoMissing); Assert.False(gone.ShowPhoto);
        Assert.False(new ChatBubble(new ChatLine { Body = "hi" }, true).PhotoMissing);
    }

    [Fact]
    public void Storage_names_keep_the_folder_and_replace_odd_characters_in_the_file_part()
    {
        Assert.Equal("a/b/20261016-my_slip__1_.png", StoragePath.Safe("a/b/20261016-my slip (1).png"));
        Assert.Equal("plain.jpg", StoragePath.Safe("plain.jpg"));
    }
}
