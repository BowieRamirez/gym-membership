using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class Phase6bPhotoTests
{
    static async Task<DemoGateway> As(string role)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync($"{role}@demo.io", DemoGateway.DemoPassword);
        return gw;
    }

    sealed class Picker : IProofPicker
    {
        public (string FileName, byte[] Data)? File { get; set; } = ("photo.png", [137, 80, 78, 71]);
        public bool Throw { get; set; }
        public Task<(string FileName, byte[] Data)?> PickAsync() => Throw
            ? throw new IOException("picker failed") : Task.FromResult(File);
    }

    static CoachPhotoViewModel Photo(DemoGateway gw, Picker? picker = null) =>
        new(new CoachService(gw), new ProfileService(gw), picker ?? new Picker());

    [Fact]
    public async Task Admin_upload_changes_only_the_photo_and_keeps_live_status()
    {
        var gw = await As("admin");
        var service = new AdminService(gw);
        var before = (await service.AllAmenitiesAsync()).Value!.First(a => a.Name == "Sauna");
        var result = await service.SetAmenityPhotoAsync(before.Id, "../My sauna.PNG", [137, 80, 78, 71]);
        Assert.True(result.Ok, result.Message);
        Assert.StartsWith($"amenities/{before.Id}/", result.Value);
        Assert.EndsWith(".png", result.Value);
        var after = (await service.AllAmenitiesAsync()).Value!.Single(a => a.Id == before.Id);
        Assert.Equal(result.Value, after.ImagePath);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Price, after.Price);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.Availability, after.Availability);
        Assert.Equal(before.StatusReason, after.StatusReason);
        Assert.Equal(before.BackAt, after.BackAt);
        var second = await service.SetAmenityPhotoAsync(before.Id, "sauna.png", [1]);
        Assert.True(second.Ok);
        Assert.NotEqual(result.Value, second.Value);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("coach")]
    [InlineData("employee")]
    public async Task Only_an_admin_can_upload_amenity_photos(string role)
    {
        var gw = await As(role);
        var service = new AdminService(gw);
        var id = (await gw.ListAsync<Amenity>()).First().Id;
        Assert.Equal(AppErrorKind.Unauthorized, (await service.SetAmenityPhotoAsync(id, "pool.jpg", [1])).Error);
        Assert.Null((await gw.ListAsync<Amenity>(a => a.Id == id)).Single().ImagePath);
        await Assert.ThrowsAnyAsync<Exception>(() => gw.UploadAsync("gym-assets", "pool.jpg", [1]));
    }

    [Fact]
    public async Task Invalid_or_failed_upload_keeps_the_previous_photo()
    {
        var gw = await As("admin");
        var service = new AdminService(gw);
        var id = (await gw.ListAsync<Amenity>()).First().Id;
        var original = (await service.SetAmenityPhotoAsync(id, "pool.jpg", [1])).Value;
        foreach (var (name, data) in new (string, byte[])[] { ("x.txt", [1]), ("x.png", []), ("x.png", new byte[ProfileService.MaxBytes + 1]) })
            Assert.Equal(AppErrorKind.Validation, (await service.SetAmenityPhotoAsync(id, name, data)).Error);
        gw.FailWith = new HttpRequestException("offline");
        Assert.Equal(AppErrorKind.Offline, (await service.SetAmenityPhotoAsync(id, "new.jpg", [2])).Error);
        gw.FailWith = null;
        Assert.Equal(original, (await gw.ListAsync<Amenity>(a => a.Id == id)).Single().ImagePath);
    }

    [Fact]
    public async Task Amenity_cards_resolve_photos_and_refresh_when_the_photo_changes()
    {
        var gw = await As("admin");
        var svc = new AmenityService(gw);
        var before = (await svc.BoardAsync()).Value!.Cards.First();
        Assert.Null(before.PhotoUrl);
        await new AdminService(gw).SetAmenityPhotoAsync(before.Amenity.Id, "pool.jpg", [1]);
        var after = (await svc.BoardAsync()).Value!.Cards.Single(a => a.Amenity.Id == before.Amenity.Id);
        Assert.False(before.SameAs(after));
        Assert.NotNull(after.PhotoUrl);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        Assert.NotNull((await svc.BoardAsync()).Value!.Cards.Single(a => a.Amenity.Id == before.Amenity.Id).PhotoUrl);
    }

    [Fact]
    public async Task Coach_cards_show_the_existing_avatar_without_changing_hire_rules()
    {
        var gw = await As("coach");
        var uid = gw.CurrentUserId;
        await new ProfileService(gw).SetAvatarAsync("coach.jpg", [1]);
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var card = (await new CoachService(gw).ListCoachesAsync()).Value!.Single(c => c.Coach.UserId == uid);
        Assert.NotNull(card.PhotoUrl);
        Assert.Equal(Initials.From(card.Name), card.Letters);
    }

    [Fact]
    public async Task Missing_links_do_not_hide_the_coach_or_amenity_cards()
    {
        var gw = await As("admin");
        var amenity = (await gw.ListAsync<Amenity>()).First();
        await gw.UpdateWhereAsync<Amenity, string?>(a => a.Id == amenity.Id, a => a.ImagePath, "missing.jpg");
        var board = await new AmenityService(gw).BoardAsync();
        Assert.True(board.Ok);
        Assert.Null(board.Value!.Cards.Single(c => c.Amenity.Id == amenity.Id).PhotoUrl);
        Assert.NotEmpty((await new CoachService(gw).ListCoachesAsync()).Value!);
    }

    [Fact]
    public async Task A_coach_is_prompted_until_the_photo_upload_succeeds()
    {
        var gw = await As("coach");
        var vm = Photo(gw);
        await vm.OnAppearingAsync();
        Assert.True(vm.IsCoach);
        Assert.True(vm.NeedsPhoto);
        await vm.ChangePhotoCommand.ExecuteAsync(null);
        Assert.False(vm.NeedsPhoto);
        Assert.NotNull(vm.PhotoUrl);
        Assert.Null(vm.ErrorMessage);
        Assert.NotNull((await new ProfileService(gw).MeAsync()).Value!.AvatarPath);
    }

    [Fact]
    public async Task Members_never_see_the_coach_prompt()
    {
        var vm = Photo(await As("member"));
        await vm.OnAppearingAsync();
        Assert.False(vm.IsCoach);
        Assert.False(vm.NeedsPhoto);
    }

    [Fact]
    public async Task Cancelling_or_failing_the_picker_preserves_the_reminder()
    {
        var gw = await As("coach");
        var picker = new Picker { File = null };
        var vm = Photo(gw, picker);
        await vm.OnAppearingAsync();
        await vm.ChangePhotoCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsPhoto);
        Assert.Null(vm.ErrorMessage);
        picker.Throw = true;
        await vm.ChangePhotoCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsPhoto);
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Profile_and_landing_screen_load_the_shared_photo_model()
    {
        var gw = await As("coach");
        var profile = new CoachProfileViewModel(new CoachService(gw), Photo(gw));
        await profile.OnAppearingAsync();
        Assert.True(profile.Photo!.NeedsPhoto);
        var sessions = new SessionsViewModel(new SessionService(gw), Photo(gw));
        await sessions.OnAppearingAsync();
        Assert.True(sessions.Photo!.NeedsPhoto);
    }

    [Fact]
    public async Task A_stale_catalog_toggle_does_not_clear_a_newer_photo()
    {
        var gw = await As("admin");
        var svc = new AdminService(gw);
        var stale = (await gw.ListAsync<Amenity>()).First();
        var path = (await svc.SetAmenityPhotoAsync(stale.Id, "new.jpg", [1])).Value;
        stale.IsActive = !stale.IsActive;
        Assert.True((await svc.SaveAmenityAsync(stale)).Ok);
        Assert.Equal(path, (await gw.ListAsync<Amenity>(a => a.Id == stale.Id)).Single().ImagePath);
    }

    [Fact]
    public async Task Rapid_coach_photo_replacements_have_distinct_paths()
    {
        var gw = await As("coach");
        var profile = new ProfileService(gw);
        var first = await profile.SetAvatarAsync("a.png", [1]);
        var second = await profile.SetAvatarAsync("a.png", [2]);
        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.NotEqual(first.Value, second.Value);
    }

    [Fact]
    public async Task Failed_coach_upload_keeps_the_reminder_and_can_be_retried()
    {
        var gw = await As("coach");
        var vm = Photo(gw);
        await vm.OnAppearingAsync();
        gw.FailWith = new HttpRequestException("offline");
        await vm.ChangePhotoCommand.ExecuteAsync(null);
        Assert.True(vm.NeedsPhoto);
        Assert.NotNull(vm.ErrorMessage);
        gw.FailWith = null;
        await vm.ChangePhotoCommand.ExecuteAsync(null);
        Assert.False(vm.NeedsPhoto);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task An_inactive_admin_cannot_upload_photos()
    {
        var gw = await As("admin");
        var uid = gw.CurrentUserId!;
        var id = (await gw.ListAsync<Amenity>()).First().Id;
        var admin = new AdminService(gw);
        await gw.SignUpAsync("second-admin@demo.io", DemoGateway.DemoPassword, "Second", "Admin");
        var secondId = gw.CurrentUserId!;
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await admin.AssignRoleAsync(secondId, "admin")).Ok);
        await gw.SignInAsync("second-admin@demo.io", DemoGateway.DemoPassword);
        Assert.True((await admin.SetActiveAsync(uid, false)).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        Assert.False((await admin.SetAmenityPhotoAsync(id, "pool.png", [1])).Ok);
        await Assert.ThrowsAnyAsync<Exception>(() => gw.UploadAsync("gym-assets", "pool.png", [1]));
    }

    [Fact]
    public async Task Admin_catalog_upload_command_saves_the_selected_amenity()
    {
        var gw = await As("admin");
        var vm = new CatalogAdminViewModel(new AdminService(gw), new Picker());
        await vm.OnAppearingAsync();
        var selected = vm.Amenities.First();
        await vm.UploadAmenityPhotoCommand.ExecuteAsync(selected);
        Assert.NotNull(vm.Amenities.Single(a => a.Id == selected.Id).ImagePath);
        Assert.NotNull(vm.Notice);
        Assert.Null(vm.ErrorMessage);
    }
}
