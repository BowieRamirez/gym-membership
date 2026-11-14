using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Item 13: each master data entity through the app's own services, end to end. Delete means archive, and archiving keeps the record.</summary>
public class CrudTests
{
    static async Task<DemoGateway> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    [Fact]
    public async Task A_package_is_created_read_updated_archived_and_restored()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw); var archive = new ArchiveService(gw);
        Assert.True((await admin.SavePackageAsync(new MembershipPackage { Name = "Trial", Price = 100, DurationDays = 7 })).Ok);
        var plan = (await admin.AllPackagesAsync()).Value!.Single(p => p.Name == "Trial");

        plan.Price = 150;
        Assert.True((await admin.SavePackageAsync(plan)).Ok);
        Assert.Equal(150, (await admin.AllPackagesAsync()).Value!.Single(p => p.Name == "Trial").Price);

        Assert.True((await admin.DeletePackageAsync(plan)).Ok);
        Assert.False((await admin.AllPackagesAsync()).Value!.Single(p => p.Name == "Trial").IsActive);
        var row = (await archive.ListAsync(new(Type: "plan"), 0)).Value!.Rows.Single(r => r.RecordId == plan.Id.ToString());
        Assert.True((await archive.RestoreAsync(row)).Ok);
        Assert.True((await admin.AllPackagesAsync()).Value!.Single(p => p.Name == "Trial").IsActive);
    }

    [Fact]
    public async Task An_amenity_is_created_read_and_updated()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        Assert.True((await admin.SaveAmenityAsync(new Amenity { Name = "Crud room", Price = 200, DurationDays = 30 })).Ok);
        var sauna = (await admin.AllAmenitiesAsync()).Value!.Single(a => a.Name == "Crud room");

        sauna.Price = 250;
        Assert.True((await admin.SaveAmenityAsync(sauna)).Ok);
        Assert.Equal(250, (await admin.AllAmenitiesAsync()).Value!.Single(a => a.Name == "Crud room").Price);
    }

    [Fact]
    public async Task Deleting_an_amenity_archives_it_and_keeps_passes_already_sold()
    {
        var gw = await As("admin@demo.io");
        var vm = new CatalogAdminViewModel(new AdminService(gw)); var archive = new ArchiveService(gw);
        await vm.OnAppearingAsync();
        var amenityRow = vm.Amenities.First();
        var amenity = amenityRow.Amenity;
        var passes = (await gw.ListAsync<UserAmenity>()).Count;

        await vm.RunActionCommand.ExecuteAsync((amenityRow, "Archive"));
        Assert.Same(amenity, vm.DeletingAmenity);
        vm.KeepAmenityCommand.Execute(null);
        Assert.Null(vm.DeletingAmenity);
        Assert.Contains(amenityRow, vm.Amenities);

        await vm.RunActionCommand.ExecuteAsync((amenityRow, "Archive"));
        await vm.ConfirmDeleteAmenityCommand.ExecuteAsync(null);
        Assert.Null(vm.DeletingAmenity);
        Assert.DoesNotContain(vm.Amenities, r => r.Amenity.Id == amenity.Id);
        Assert.Single(await gw.ListAsync<Amenity>(a => a.Id == amenity.Id));   // the row is still there
        Assert.Equal(passes, (await gw.ListAsync<UserAmenity>()).Count);

        var row = (await archive.ListAsync(new(Type: "amenity"), 0)).Value!.Rows.Single(r => r.RecordId == amenity.Id.ToString());
        Assert.Contains("amenity", row.Reason);
        Assert.True((await archive.RestoreAsync(row)).Ok);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Contains(vm.Amenities, r => r.Amenity.Id == amenity.Id);
    }

    [Fact]
    public async Task An_admin_cannot_delete_an_amenity_only_a_system_admin_can()
    {
        var gw = await As("subadmin@demo.io");
        var vm = new CatalogAdminViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        var amenity = vm.Amenities.First().Amenity;
        Assert.DoesNotContain("Archive", vm.ActionsFor(vm.Amenities.First()));
        Assert.False((await new AdminService(gw).DeleteAmenityAsync(amenity)).Ok);
    }

    // G1 (0060): turning a code off is not archiving it; it stays on the Discounts page and comes back on
    [Fact]
    public async Task A_code_turned_off_stays_on_the_page_and_turns_back_on()
    {
        var gw = await As("admin@demo.io");
        var vm = new DiscountsViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        var row = vm.Discounts.Single(r => r.Discount.Code == "WELCOME10");
        Assert.Equal(["Turn off", "Archive", "Delete"], vm.ActionsFor(row));   // never used, so it can still be deleted

        await vm.RunActionCommand.ExecuteAsync((row, "Turn off"));
        row = vm.Discounts.Single(r => r.Discount.Code == "WELCOME10");
        Assert.Equal(("Turned off", "Turn on"), (row.StatusText, vm.ActionsFor(row)[0]));
        Assert.Empty((await new ArchiveService(gw).ListAsync(new(Type: "code"), 0)).Value!.Rows.Where(r => r.RecordId == row.Discount.Id.ToString()));

        await vm.RunActionCommand.ExecuteAsync((row, "Turn on"));
        Assert.False(vm.Discounts.Single(r => r.Discount.Code == "WELCOME10").Discount.Paused);
    }

    [Fact]
    public async Task A_discount_code_is_created_read_archived_and_restored()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw); var archive = new ArchiveService(gw);
        var vm = new DiscountsViewModel(admin) { NewCode = "crud20", NewPercent = 20, NewLimit = 5, ValidUntil = DateTime.Today.AddDays(10) };
        await vm.OnAppearingAsync();
        vm.AllPackages = true;
        await vm.AddCommand.ExecuteAsync(null);
        var row = vm.Discounts.Single(r => r.Discount.Code == "CRUD20");
        Assert.Equal(DateTime.Today.AddDays(10), row.LastDay.ToLocalTime().Date);   // the day chosen, not the midnight after it
        Assert.Equal("All packages", row.FitsText);

        await vm.RunActionCommand.ExecuteAsync((row, "Archive"));
        Assert.DoesNotContain(vm.Discounts, r => r.Discount.Code == "CRUD20");
        var archived = (await archive.ListAsync(new(Type: "code"), 0)).Value!.Rows.Single(r => r.RecordId == row.Discount.Id.ToString());
        Assert.True((await archive.RestoreAsync(archived)).Ok);
        Assert.True((await gw.ListAsync<Discount>(d => d.Id == row.Discount.Id)).Single().IsActive);
    }

    // G2 (0060): nothing about a code can change after it's made, through any path
    [Fact]
    public async Task A_code_cannot_be_edited_after_it_is_made()
    {
        var gw = await As("admin@demo.io");
        var welcome = (await gw.ListAsync<Discount>()).Single(d => d.Code == "WELCOME10");
        welcome.Percent = 50; welcome.ExpiresAt = welcome.ExpiresAt.AddDays(30);
        await Assert.ThrowsAnyAsync<Exception>(() => gw.UpdateAsync(welcome));
        Assert.Equal(10, (await gw.ListAsync<Discount>(d => d.Code == "WELCOME10")).Single().Percent);
        Assert.Equal("A code can't be changed once it's made. Turn it off and add a new code with the terms you need.",
            ErrorText.For(AppErrorKind.Validation, "discount_locked"));
    }

    [Fact]
    public async Task An_account_is_created_read_updated_archived_and_restored()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw); var archive = new AccountArchiveService(gw); var records = new ArchiveService(gw);
        Assert.True((await admin.CreateUserAsync("Ana", "Cruz", "ana.crud@demo.io", "member", "temp1234")).Ok);
        var user = (await admin.UsersAsync()).Value!.Single(u => u.Username == "Ana Cruz");

        Assert.True((await admin.CorrectNameAsync(user.UserId, "Anna", "Cruz", "Typo")).Ok);
        Assert.True((await admin.ResetPasswordAsync(user.UserId, "temp5678")).Ok);
        Assert.True((await admin.SetActiveAsync(user.UserId, false)).Ok);
        Assert.False((await admin.UsersAsync()).Value!.Single(u => u.UserId == user.UserId).IsActive);
        Assert.True((await admin.SetActiveAsync(user.UserId, true)).Ok);

        Assert.True((await archive.DeleteAsync(user.UserId, "Duplicate account")).Ok);
        Assert.DoesNotContain((await admin.UsersAsync()).Value!, u => u.UserId == user.UserId);
        var row = (await records.ListAsync(new(Type: "account"), 0)).Value!.Rows.Single(r => r.RecordId == user.UserId);
        Assert.True((await records.RestoreAsync(row)).Ok);
        Assert.Contains((await admin.UsersAsync()).Value!, u => u.UserId == user.UserId);
    }

    [Fact]
    public async Task Gym_settings_are_read_and_updated()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var settings = (await admin.GymSettingsAsync()).Value!;
        settings.GymName = "Crud Gym";
        Assert.True((await admin.SaveGymSettingsAsync(settings, null)).Ok);
        Assert.Equal("Crud Gym", (await admin.GymSettingsAsync()).Value!.GymName);
    }

    [Fact]
    public async Task A_coach_profile_is_read_and_updated()
    {
        var gw = await As("coach@demo.io");
        var coaches = new CoachService(gw);
        Assert.True((await coaches.UpdateProfileAsync("New bio", "Strength", 500, true)).Ok);
        var me = (await coaches.MyCoachAsync()).Value!;
        Assert.Equal(("New bio", "Strength", 500m), (me.Bio, me.Specialty, me.HourlyRate));
    }

    [Fact]
    public async Task A_shift_is_posted_read_and_cancelled_and_stays_in_history()
    {
        var gw = await As("coach@demo.io");
        var coaches = new CoachService(gw);
        var start = new DateTime(DateTime.UtcNow.AddDays(3).Date.Ticks, DateTimeKind.Utc).AddHours(2);   // 10:00 in Manila, inside gym hours
        Assert.True((await coaches.PostShiftAsync(start, start.AddHours(2), "Crud shift")).Ok);
        var shift = (await coaches.MyShiftsAsync()).Value!.Single(s => s.Note == "Crud shift");

        Assert.True((await coaches.CancelShiftAsync(shift.Id)).Ok);
        Assert.Equal(Status.Cancelled, (await gw.ListAsync<ShiftPost>(s => s.Id == shift.Id)).Single().Status);
    }

    [Fact]
    public void The_app_has_no_way_to_physically_delete_a_row() => Assert.Null(typeof(ISupabaseGateway).GetMethod("DeleteAsync"));

    [Fact]
    public async Task People_cannot_edit_their_own_profile_or_member_row_directly()
    {
        var gw = await As("member@demo.io");
        var me = (await gw.ListAsync<Profile>(p => p.Id == gw.CurrentUserId)).Single();
        me.Phone = "09170000000"; me.FullName = "Changed Name";
        await Assert.ThrowsAsync<Exception>(() => gw.UpdateAsync(me));
        var member = (await gw.ListAsync<Member>(m => m.UserId == gw.CurrentUserId)).Single();
        member.Goals = "Run 5k";
        await Assert.ThrowsAsync<Exception>(() => gw.UpdateAsync(member));
    }
}
