using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

public class ProfileTests
{
    static async Task<(DemoGateway Gw, ProfileService Svc)> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new ProfileService(gw));
    }

    [Theory]
    [InlineData("Mary-Ann O'Brien", "MO")]
    [InlineData("Juan dela Cruz", "JC")]
    [InlineData("alex", "A")]
    [InlineData("  alex   tan ", "AT")]
    [InlineData("   ", "?")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials_come_from_the_first_and_last_word(string? name, string expected) => Assert.Equal(expected, Initials.From(name));

    [Fact]
    public async Task Me_returns_the_name_and_no_picture_at_first()
    {
        var (_, svc) = await As("member@demo.io");
        var me = (await svc.MeAsync()).Value!;
        Assert.Equal("Alex Tan", me.Name);
        Assert.Null(me.AvatarPath);
    }

    [Fact]
    public async Task A_new_picture_is_stored_in_your_own_folder_and_announced()
    {
        var (_, svc) = await As("member@demo.io");
        var announced = 0;
        svc.AvatarChanged += () => announced++;

        var path = (await svc.SetAvatarAsync("My Photo.JPG", [1, 2, 3])).Value!;

        Assert.StartsWith("u-alex/", path);
        Assert.EndsWith(".jpg", path);
        Assert.Equal(path, (await svc.MeAsync()).Value!.AvatarPath);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task Files_that_are_not_pictures_are_empty_or_too_big_are_refused_and_nothing_changes()
    {
        var (_, svc) = await As("member@demo.io");
        var announced = 0;
        svc.AvatarChanged += () => announced++;

        foreach (var (name, data) in new (string, byte[])[] { ("notes.pdf", [1]), ("photo.jpg", []), ("photo.png", new byte[ProfileService.MaxBytes + 1]), ("noextension", [1]) })
        {
            var r = await svc.SetAvatarAsync(name, data);
            Assert.Equal(AppErrorKind.Validation, r.Error);
            Assert.Equal("bad_image", r.Message);
        }
        Assert.Null((await svc.MeAsync()).Value!.AvatarPath);
        Assert.Equal(0, announced);
        Assert.Contains("5 MB", ErrorText.For(AppErrorKind.Validation, "bad_image"));
        Assert.Equal(AppErrorKind.Validation, ErrorMapper.Map(new Exception("bad_image")));
    }

    [Fact]
    public async Task No_picture_or_no_link_means_no_url_so_the_initials_show()
    {
        var (gw, svc) = await As("member@demo.io");
        Assert.Null((await svc.AvatarUrlAsync(null)).Value);
        Assert.Null((await svc.AvatarUrlAsync("")).Value);
        Assert.Null((await svc.AvatarUrlAsync("u-alex/avatar.jpg")).Value);   // the demo keeps no files
        Assert.True((await svc.AvatarUrlAsync("u-alex/avatar.jpg")).Ok);

        gw.FailWith = new HttpRequestException("offline");
        var offline = await svc.AvatarUrlAsync("u-alex/avatar.jpg");
        Assert.Equal(AppErrorKind.Offline, offline.Error);   // the caller shows initials and no banner
    }

    [Fact]
    public async Task A_failed_upload_changes_nothing_and_is_not_announced()
    {
        var (gw, svc) = await As("member@demo.io");
        var announced = 0;
        svc.AvatarChanged += () => announced++;
        gw.FailWith = new HttpRequestException("offline");

        Assert.Equal(AppErrorKind.Offline, (await svc.SetAvatarAsync("a.jpg", [1])).Error);
        Assert.Equal(0, announced);
    }
}
