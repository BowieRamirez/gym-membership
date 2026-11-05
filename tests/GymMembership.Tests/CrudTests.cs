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
        var amenity = vm.Amenities.First();
        var passes = (await gw.ListAsync<UserAmenity>()).Count;

        vm.DeleteAmenityCommand.Execute(amenity);
        Assert.Same(amenity, vm.DeletingAmenity);
        vm.KeepAmenityCommand.Execute(null);
        Assert.Null(vm.DeletingAmenity);
        Assert.Contains(amenity, vm.Amenities);

        vm.DeleteAmenityCommand.Execute(amenity);
        await vm.ConfirmDeleteAmenityCommand.ExecuteAsync(null);
        Assert.Null(vm.DeletingAmenity);
        Assert.DoesNotContain(vm.Amenities, a => a.Id == amenity.Id);
        Assert.Single(await gw.ListAsync<Amenity>(a => a.Id == amenity.Id));   // the row is still there
        Assert.Equal(passes, (await gw.ListAsync<UserAmenity>()).Count);

        var row = (await archive.ListAsync(new(Type: "amenity"), 0)).Value!.Rows.Single(r => r.RecordId == amenity.Id.ToString());
        Assert.Contains("amenity", row.Reason);
        Assert.True((await archive.RestoreAsync(row)).Ok);
        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Contains(vm.Amenities, a => a.Id == amenity.Id);
    }

    [Fact]
    public async Task An_admin_cannot_delete_an_amenity_only_a_system_admin_can()
    {
        var gw = await As("subadmin@demo.io");
        var vm = new CatalogAdminViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        var amenity = vm.Amenities.First();
        vm.DeleteAmenityCommand.Execute(amenity);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.DeletingAmenity);
        Assert.False((await new AdminService(gw).DeleteAmenityAsync(amenity)).Ok);
    }

    [Fact]
    public async Task An_inactive_code_is_already_in_the_archive_and_can_be_restored()
    {
        var gw = await As("admin@demo.io");
        var vm = new DiscountsViewModel(new AdminService(gw)); var archive = new ArchiveService(gw);
        await vm.OnAppearingAsync();
        var row = vm.Discounts.First();

        await vm.ToggleCommand.ExecuteAsync(row);
        Assert.DoesNotContain(vm.Discounts, r => r.Discount.Id == row.Discount.Id);
        var archived = (await archive.ListAsync(new(Type: "code"), 0)).Value!.Rows.Single(r => r.RecordId == row.Discount.Id.ToString());
        Assert.NotNull(archived.ArchivedAt);
        Assert.Single(await gw.ListAsync<Discount>(d => d.Id == row.Discount.Id));   // the row is still there
        Assert.True((await archive.RestoreAsync(archived)).Ok);
    }

    [Fact]
    public async Task A_discount_code_is_created_read_edited_archived_and_restored()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw); var archive = new ArchiveService(gw);
        var vm = new DiscountsViewModel(admin) { NewCode = "crud20", NewPercent = 20, NewLimit = 5, ValidUntil = DateTime.Today.AddDays(10) };
        await vm.AddCommand.ExecuteAsync(null);
        var row = vm.Discounts.Single(r => r.Discount.Code == "CRUD20");
        Assert.Equal(DateTime.Today.AddDays(10), row.LastDay.ToLocalTime().Date);   // the day chosen, not the midnight after it

        vm.EditCodeCommand.Execute(row);
        Assert.Equal((5, DateTime.Today.AddDays(10)), (vm.EditLimit, vm.EditUntil));
        vm.EditUntil = DateTime.Today.AddDays(40);
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.Null(vm.Editing);
        row = vm.Discounts.Single(r => r.Discount.Code == "CRUD20");
        Assert.Equal((5, DateTime.Today.AddDays(40)), (row.Discount.MaxUses, row.LastDay.ToLocalTime().Date));

        vm.DeleteCommand.Execute(row);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Discounts, r => r.Discount.Code == "CRUD20");
        var archived = (await archive.ListAsync(new(Type: "code"), 0)).Value!.Rows.Single(r => r.RecordId == row.Discount.Id.ToString());
        Assert.True((await archive.RestoreAsync(archived)).Ok);
        Assert.True((await gw.ListAsync<Discount>(d => d.Id == row.Discount.Id)).Single().IsActive);
    }

    [Fact]
    public async Task Changing_only_the_date_leaves_an_unlimited_code_unlimited()
    {
        var gw = await As("admin@demo.io");
        var admin = new AdminService(gw);
        var vm = new DiscountsViewModel(admin);
        await vm.OnAppearingAsync();
        var row = vm.Discounts.First();
        row.Discount.MaxUses = null;
        Assert.True((await admin.SaveDiscountAsync(row.Discount)).Ok);
        await vm.LoadCommand.ExecuteAsync(null);
        row = vm.Discounts.Single(r => r.Discount.Id == row.Discount.Id);

        vm.EditCodeCommand.Execute(row);
        vm.EditUntil = DateTime.Today.AddDays(90);
        await vm.SaveEditCommand.ExecuteAsync(null);
        row = vm.Discounts.Single(r => r.Discount.Id == row.Discount.Id);
        Assert.Null(row.Discount.MaxUses);
        Assert.Equal(DateTime.Today.AddDays(90), row.LastDay.ToLocalTime().Date);
    }

    [Fact]
    public async Task A_past_end_date_is_refused_and_the_code_is_unchanged()
    {
        var gw = await As("admin@demo.io");
        var vm = new DiscountsViewModel(new AdminService(gw));
        await vm.OnAppearingAsync();
        var row = vm.Discounts.First(); var before = row.Discount.ExpiresAt;
        vm.EditCodeCommand.Execute(row);
        vm.EditUntil = DateTime.Today.AddDays(-1);
        await vm.SaveEditCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.NotNull(vm.Editing);
        Assert.Equal(before, row.Discount.ExpiresAt);
    }

    [Theory]
    [InlineData("OTHER", 10)]
    [InlineData("WELCOME10", 50)]
    public async Task A_code_and_its_percent_cannot_change(string code, int percent)
    {
        var gw = await As("admin@demo.io");
        var welcome = (await gw.ListAsync<Discount>()).Single(d => d.Code == "WELCOME10");
        var edited = new Discount { Id = welcome.Id, Code = code, Percent = percent, StartsAt = welcome.StartsAt, ExpiresAt = welcome.ExpiresAt, MaxUses = welcome.MaxUses, IsActive = true };
        var result = await new AdminService(gw).SaveDiscountAsync(edited);
        Assert.Equal("discount_fixed", result.Message);
        Assert.Equal(AppErrorKind.Validation, result.Error);
        Assert.Equal("A code and its percent can't change. To change them, archive this code and add a new one.", ErrorText.For(result.Error, result.Message));
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
