using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MessengerPhotoTests
{
    sealed class FakePicker : IProofPicker
    {
        public (string FileName, byte[] Data)? Next;
        public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult(Next);
    }

    static async Task<(DemoGateway Gw, FakePicker Picker, MessengerViewModel Vm)> Open(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var picker = new FakePicker { Next = ("squat.png", DemoPng.Make()) };
        var vm = new MessengerViewModel(new ChatService(gw, new NotificationService(gw)), new ProfileService(gw), new AuthService(gw), picker);
        await vm.OnAppearingAsync();
        return (gw, picker, vm);
    }

    [Fact]
    public async Task Choosing_a_photo_shows_it_as_a_chip_and_sending_posts_it_with_the_caption_and_clears_both()
    {
        var (_, _, vm) = await Open("member@demo.io");

        await vm.AttachCommand.ExecuteAsync(null);
        Assert.True(vm.HasPhoto); Assert.Equal("squat.png", vm.PhotoName);

        vm.Draft = "how is my depth?";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.False(vm.HasPhoto); Assert.Equal("", vm.Draft);
        var last = vm.Bubbles.Last();
        Assert.True(last.ShowPhoto); Assert.Equal("how is my depth?", last.Line.Body); Assert.True(last.Mine);
        Assert.Equal("You: how is my depth?", vm.Threads.Single().Thread.Preview);
    }

    [Fact]
    public async Task A_photo_alone_can_be_sent_and_the_list_says_Photo()
    {
        var (_, _, vm) = await Open("member@demo.io");
        await vm.AttachCommand.ExecuteAsync(null);

        await vm.SendCommand.ExecuteAsync(null);   // no caption

        Assert.True(vm.Bubbles.Last().ShowPhoto); Assert.False(vm.Bubbles.Last().HasText);
        Assert.Equal("You: Photo", vm.Threads.Single().Thread.Preview);
    }

    [Fact]
    public async Task Remove_drops_the_chosen_photo_and_a_wrong_file_is_refused_with_a_plain_message()
    {
        var (_, picker, vm) = await Open("member@demo.io");
        await vm.AttachCommand.ExecuteAsync(null);
        vm.RemovePhotoCommand.Execute(null);
        Assert.False(vm.HasPhoto);

        picker.Next = ("notes.pdf", [1, 2, 3]);
        await vm.AttachCommand.ExecuteAsync(null);
        Assert.False(vm.HasPhoto);
        Assert.Equal("Choose a JPG, PNG or WebP picture under 5 MB.", vm.ErrorMessage);

        picker.Next = null;   // the person cancelled the picker
        vm.ErrorMessage = null;
        await vm.AttachCommand.ExecuteAsync(null);
        Assert.False(vm.HasPhoto); Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Offline_the_caption_and_the_chosen_photo_stay_and_no_bubble_appears()
    {
        var (gw, _, vm) = await Open("member@demo.io");
        var before = vm.Bubbles.Count;
        await vm.AttachCommand.ExecuteAsync(null);
        vm.Draft = "sending";
        gw.FailWith = new HttpRequestException("offline");

        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.True(vm.HasPhoto); Assert.Equal("sending", vm.Draft);
        Assert.Equal(before, vm.Bubbles.Count);
    }

    [Fact]
    public async Task A_chosen_photo_does_not_follow_you_into_another_conversation()
    {
        var (_, _, vm) = await Open("member@demo.io");
        await vm.AttachCommand.ExecuteAsync(null);

        await vm.ShowTabCommand.ExecuteAsync("support");   // the front desk conversation
        Assert.False(vm.HasPhoto);
        await vm.SendCommand.ExecuteAsync(null);           // nothing to send: no text and no photo
        Assert.Empty(vm.Bubbles);
    }

    [Fact]
    public async Task A_member_sends_a_photo_to_the_front_desk_and_staff_see_it()
    {
        var (gw, _, vm) = await Open("member@demo.io");
        await vm.ShowTabCommand.ExecuteAsync("support");
        await vm.AttachCommand.ExecuteAsync(null);
        vm.Draft = "my slip";
        await vm.SendCommand.ExecuteAsync(null);

        await gw.SignOutAsync(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new MessengerViewModel(new ChatService(gw, new NotificationService(gw)), new ProfileService(gw), new AuthService(gw), new FakePicker());
        await desk.OnAppearingAsync();
        await desk.OpenCommand.ExecuteAsync(desk.Threads.Single());
        Assert.True(desk.Bubbles.Last().ShowPhoto);
        Assert.Equal("my slip", desk.Bubbles.Last().Line.Body);
    }

    [Fact]
    public void The_demo_picture_is_a_real_png_the_app_accepts()
    {
        var png = DemoPng.Make();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.True(ProfileService.IsAcceptableImage("demo-photo.png", png));
        Assert.Equal("demo-photo.png", new DemoPhotoPicker().PickAsync().Result!.Value.FileName);
    }
}
