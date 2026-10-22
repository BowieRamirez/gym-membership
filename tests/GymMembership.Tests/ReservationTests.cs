using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 6: the booking grid in demo mode. Alex (member@demo.io) holds an active Pool pass; Bea does not.
public class ReservationTests
{
    static async Task<(DemoGateway Gw, BookAmenityViewModel Vm)> Member(string email = "member@demo.io")
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new BookAmenityViewModel(new ReservationService(gw));
        vm.Day = DateTime.UtcNow.AddHours(8).Date.AddDays(1);   // tomorrow in Manila: every slot is in the future
        await vm.LoadCommand.ExecuteAsync(null);
        return (gw, vm);
    }

    static SlotCell Cell(BookAmenityViewModel vm, string amenity, string time) =>
        vm.Rows.Single(r => r.Time == time).Cells[vm.Amenities.IndexOf(amenity)];

    [Fact]
    public async Task The_grid_has_a_row_per_half_hour_of_gym_hours_and_a_column_per_amenity()
    {
        var (_, vm) = await Member();
        Assert.Equal(32, vm.Rows.Count);                                        // 06:00 to 22:00
        Assert.Equal(("06:00", "21:30"), (vm.Rows[0].Time, vm.Rows[^1].Time));
        Assert.Contains("Pool", vm.Amenities);
        Assert.DoesNotContain("Boxing ring", vm.Amenities);                   // inactive amenities are not bookable
        Assert.Equal("open", Cell(vm, "Pool", "10:00").State);
    }

    [Fact]
    public async Task Touching_slots_book_as_one_session_followed_by_a_cooldown()
    {
        var (_, vm) = await Member();
        vm.ToggleCommand.Execute(Cell(vm, "Pool", "10:00"));
        vm.ToggleCommand.Execute(Cell(vm, "Pool", "10:30"));
        Assert.Equal("Book selected slots (2)", vm.BookText);
        await vm.BookCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal("Booked.", vm.Notice);
        var booking = Assert.Single(vm.Bookings);
        Assert.EndsWith("10:00–11:00", booking.When);
        Assert.Equal(("yours", "yours", "cooldown", "open"), (Cell(vm, "Pool", "10:00").State, Cell(vm, "Pool", "10:30").State,
            Cell(vm, "Pool", "11:00").State, Cell(vm, "Pool", "11:30").State));
        Assert.Equal("blocked", Cell(vm, "Pool", "09:30").State);             // its cooldown would run into 10:00
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Without_a_pass_nothing_is_booked_and_the_reason_stays_on_screen()
    {
        var (_, vm) = await Member("bea@demo.io");
        vm.ToggleCommand.Execute(Cell(vm, "Pool", "10:00"));
        await vm.BookCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.Notice);
        Assert.Empty(vm.Bookings);
        Assert.Equal("open", Cell(vm, "Pool", "10:00").State);
    }

    [Fact]
    public async Task A_conflict_books_nothing_refreshes_the_grid_and_keeps_the_error()
    {
        var (gw, vm) = await Member();
        vm.ToggleCommand.Execute(Cell(vm, "Pool", "14:00"));
        vm.ToggleCommand.Execute(Cell(vm, "Pool", "17:00"));
        // meanwhile 14:30 is booked, so 14:00's cooldown would run into it
        Assert.True((await new ReservationService(gw).BookAsync([(Cell(vm, "Pool", "14:30").AmenityId, Cell(vm, "Pool", "14:30").StartUtc)])).Ok);

        await vm.BookCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Single(vm.Bookings);                                            // only the 14:30 one: 17:00 was not booked either
        Assert.Equal("blocked", Cell(vm, "Pool", "14:00").State);
        Assert.False(Cell(vm, "Pool", "14:00").Selected);
        Assert.True(Cell(vm, "Pool", "17:00").Selected);                      // still open, so the pick survives the refresh
    }

    [Fact]
    public async Task Cancelling_frees_the_time_and_its_cooldown()
    {
        var (_, vm) = await Member();
        vm.ToggleCommand.Execute(Cell(vm, "Pool", "08:00"));
        await vm.BookCommand.ExecuteAsync(null);
        await vm.CancelCommand.ExecuteAsync(vm.Bookings.Single());
        Assert.Equal("Booking cancelled.", vm.Notice);
        Assert.Empty(vm.Bookings);
        Assert.Equal(("open", "open"), (Cell(vm, "Pool", "08:00").State, Cell(vm, "Pool", "08:30").State));
    }

    [Fact]
    public async Task Other_members_bookings_show_as_booked_without_who()
    {
        var (gw, vm) = await Member();
        vm.ToggleCommand.Execute(Cell(vm, "Pool", "12:00"));
        await vm.BookCommand.ExecuteAsync(null);

        await gw.SignInAsync("bea@demo.io", DemoGateway.DemoPassword);
        var grid = (await new ReservationService(gw).GridAsync(DateOnly.FromDateTime(vm.Day))).Value!;
        var block = Assert.Single(grid.Blocks);
        Assert.False(block.Mine);
        Assert.Null(block.Id);
    }
}
