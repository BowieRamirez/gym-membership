using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class AmenityViewModelTests
{
    static async Task<(DemoGateway Gw, AmenityStatusViewModel Vm)> Desk()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new AmenityStatusViewModel(new AmenityService(gw));
        await vm.OnAppearingAsync();
        return (gw, vm);
    }

    [Fact]
    public async Task The_board_loads_and_a_member_can_save_an_amenity_from_it()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var vm = new AmenityBoardViewModel(new AmenityService(gw));
        await vm.OnAppearingAsync();
        Assert.True(vm.CanFollow);
        var classes = vm.Cards.First(c => c.Amenity.Name == "Group classes");
        Assert.False(classes.Followed);

        await vm.ToggleFollowCommand.ExecuteAsync(classes);

        Assert.True(vm.Cards.First(c => c.Amenity.Id == classes.Amenity.Id).Followed);
    }

    [Fact]
    public async Task A_coach_gets_the_board_without_the_save_button()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var vm = new AmenityBoardViewModel(new AmenityService(gw));
        await vm.OnAppearingAsync();
        Assert.False(vm.CanFollow);
        Assert.NotEmpty(vm.Cards);
    }

    [Fact]
    public async Task Choosing_an_amenity_fills_the_editor_with_its_current_status()
    {
        var (_, vm) = await Desk();
        var sauna = vm.Cards.First(c => c.Amenity.Name == "Sauna");

        vm.SelectCommand.Execute(sauna);

        Assert.True(vm.IsEditing);
        Assert.Equal("unavailable", vm.Choice.Value);
        Assert.Equal("Cleaning", vm.Reason);
        Assert.True(vm.HasBackAt);
        Assert.True(vm.ShowDetail);
    }

    [Fact]
    public async Task Saving_unavailable_names_the_members_who_were_alerted_and_closes_the_editor()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        vm.QuickReasonCommand.Execute("Maintenance");
        Assert.Equal("Maintenance", vm.Reason);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("Pool is now unavailable. Alerted 1 member: Alex Tan.", vm.Notice);
        Assert.Equal("unavailable", vm.Cards.First(c => c.Amenity.Name == "Pool").Availability);
    }

    [Fact]
    public async Task The_notice_says_so_when_nobody_saved_it_when_it_is_only_in_use_and_when_nothing_changed()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Group classes"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Group classes is now unavailable. Nobody had saved it, so no alerts were sent.", vm.Notice);

        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Group classes"));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Nothing changed for Group classes.", vm.Notice);

        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "in_use");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Pool is now in use. Members are not alerted for this.", vm.Notice);
    }

    [Fact]
    public async Task A_return_time_in_the_past_is_refused_on_the_screen_and_the_editor_stays_open()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        vm.HasBackAt = true;
        vm.BackDate = DateTime.Today.AddDays(-1);
        vm.BackTime = TimeSpan.FromHours(9);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Pick a return time that is still ahead.", vm.ErrorMessage);
        Assert.True(vm.IsEditing);
        Assert.Equal("available", vm.Cards.First(c => c.Amenity.Name == "Pool").Availability);
    }

    [Fact]
    public async Task Offline_the_editor_stays_open_with_what_was_typed_and_nothing_is_claimed()
    {
        var (gw, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");
        vm.Reason = "Repairs";
        gw.FailWith = new HttpRequestException("offline");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Can't reach the server. Check your connection and try again.", vm.ErrorMessage);
        Assert.True(vm.IsEditing);
        Assert.Equal("Repairs", vm.Reason);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task Cancel_closes_the_editor_without_saving()
    {
        var (_, vm) = await Desk();
        vm.SelectCommand.Execute(vm.Cards.First(c => c.Amenity.Name == "Pool"));
        vm.Choice = vm.Choices.First(c => c.Value == "unavailable");

        vm.CancelCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Equal("available", vm.Cards.First(c => c.Amenity.Name == "Pool").Availability);
    }
}
