using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class AmenityStatusTests
{
    static async Task<(DemoGateway Gw, AmenityService Svc)> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new AmenityService(gw));
    }

    [Fact]
    public async Task The_board_shows_active_amenities_with_status_and_what_the_member_saved()
    {
        var (_, svc) = await As("member@demo.io");
        var board = (await svc.BoardAsync()).Value!;

        Assert.True(board.CanFollow);
        Assert.DoesNotContain(board.Cards, c => c.Amenity.Name == "Boxing ring");   // switched off in the catalog: not shown
        var sauna = board.Cards.First(c => c.Amenity.Name == "Sauna");
        Assert.Equal("unavailable", sauna.Availability);   // the demo's cleaning break
        Assert.False(string.IsNullOrWhiteSpace(sauna.Reason));
        Assert.NotNull(sauna.BackAt);
        Assert.True(sauna.Followed);
        Assert.Equal("Following", sauna.FollowText);
        Assert.Equal("Notify me", board.Cards.First(c => c.Amenity.Name == "Group classes").FollowText);
        Assert.Equal(board.Cards.Select(c => c.Amenity.Name).Order(), board.Cards.Select(c => c.Amenity.Name));
    }

    [Fact]
    public async Task A_coach_sees_the_board_but_cannot_save_amenities()
    {
        var (_, svc) = await As("coach@demo.io");
        var coachBoard = (await svc.BoardAsync()).Value!;
        Assert.False(coachBoard.CanFollow);
        Assert.All(coachBoard.Cards, c => Assert.Equal("", c.FollowText));   // no button text: the card hides its button
        Assert.Equal(AppErrorKind.Unauthorized, (await svc.SetFollowedAsync(1, true)).Error);
    }

    [Fact]
    public async Task Saving_and_un_saving_an_amenity_is_idempotent_and_shows_on_the_board()
    {
        var (_, svc) = await As("member@demo.io");
        var classes = (await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Group classes");

        Assert.True((await svc.SetFollowedAsync(classes.Amenity.Id, true)).Ok);
        Assert.True((await svc.SetFollowedAsync(classes.Amenity.Id, true)).Ok);
        Assert.True((await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == classes.Amenity.Id).Followed);

        Assert.True((await svc.SetFollowedAsync(classes.Amenity.Id, false)).Ok);
        Assert.False((await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == classes.Amenity.Id).Followed);
    }

    [Fact]
    public async Task Front_desk_changes_a_status_and_learns_who_was_alerted_and_the_board_shows_it()
    {
        var (_, desk) = await As("employee@demo.io");
        var pool = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");

        var told = (await desk.SetStatusAsync(pool.Amenity.Id, "unavailable", "  Maintenance ", DateTime.UtcNow.AddHours(2))).Value!;

        Assert.Equal(["Alex Tan"], told.Select(t => t.Name));   // the demo member who saved the pool
        var after = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == pool.Amenity.Id);
        Assert.Equal("unavailable", after.Availability);
        Assert.Equal("Maintenance", after.Reason);
        Assert.True(after.HasBackAt);
    }

    [Fact]
    public async Task The_alert_lands_in_the_followers_alerts_and_not_in_anyone_elses()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new AmenityService(gw);
        var pool = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");
        await desk.SetStatusAsync(pool.Amenity.Id, "unavailable", "Maintenance", DateTime.UtcNow.AddHours(2));

        await gw.SignOutAsync(); await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);   // Alex saved the pool
        var alex = (await new NotificationService(gw).ListAsync()).Value!.Where(n => n.Type == "amenity").ToList();
        Assert.Contains(alex, n => n.Title == "Pool is unavailable" && n.Body!.Contains("Maintenance"));

        await gw.SignOutAsync(); await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);   // Bea saved nothing
        Assert.DoesNotContain((await new NotificationService(gw).ListAsync()).Value!, n => n.Type == "amenity");
    }

    [Fact]
    public async Task The_same_status_again_alerts_nobody_and_in_use_alerts_nobody()
    {
        var (_, desk) = await As("employee@demo.io");
        var pool = (await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");

        Assert.Empty((await desk.SetStatusAsync(pool.Amenity.Id, "available", null, null)).Value!);   // already available
        Assert.Empty((await desk.SetStatusAsync(pool.Amenity.Id, "in_use", "ignored", null)).Value!);
        Assert.Null((await desk.BoardAsync()).Value!.Cards.First(c => c.Amenity.Id == pool.Amenity.Id).Reason);   // a reason belongs to "unavailable" only
    }

    [Fact]
    public async Task Members_cannot_set_a_status_and_bad_input_is_refused_before_anything_changes()
    {
        var (_, member) = await As("member@demo.io");
        Assert.Equal(AppErrorKind.Unauthorized, (await member.SetStatusAsync(1, "unavailable", null, null)).Error);

        var (_, desk) = await As("employee@demo.io");
        Assert.Equal(AppErrorKind.Validation, (await desk.SetStatusAsync(1, "broken", null, null)).Error);
        Assert.Equal(AppErrorKind.Validation, (await desk.SetStatusAsync(1, "unavailable", new string('x', 201), null)).Error);
        Assert.Equal(AppErrorKind.Validation, (await desk.SetStatusAsync(1, "unavailable", "Cleaning", DateTime.UtcNow.AddMinutes(-5))).Error);
    }

    [Fact]
    public async Task Someone_who_un_saved_is_not_told_and_not_listed()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var member = new AmenityService(gw);
        var pool = (await member.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");
        await member.SetFollowedAsync(pool.Amenity.Id, false);

        await gw.SignOutAsync(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var told = (await new AmenityService(gw).SetStatusAsync(pool.Amenity.Id, "unavailable", "Repairs", null)).Value!;
        Assert.Empty(told);
    }

    [Fact]
    public async Task A_deactivated_member_who_saved_an_amenity_is_not_told()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        await gw.RpcAsync("set_user_active", new() { ["p_user"] = "u-alex", ["p_active"] = false });

        await gw.SignOutAsync(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var svc = new AmenityService(gw);
        var pool = (await svc.BoardAsync()).Value!.Cards.First(c => c.Amenity.Name == "Pool");
        Assert.Empty((await svc.SetStatusAsync(pool.Amenity.Id, "unavailable", "Repairs", null)).Value!);
    }
}
