using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 4 of the 2026-11-12 plan (G8, approved mockup docs/mockups/catalog-add.html): one Actions menu per row, offering only what the
// row's state allows, and one side panel to add, edit, save as draft, add to sale, and review a new version before publishing.
public class CatalogScreenTests
{
    sealed class Picker : IProofPicker
    { public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult<(string, byte[])?>(("ring.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3])); }

    static async Task<(CatalogAdminViewModel Vm, DemoGateway Gw)> Open(string email = "admin@demo.io")
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var vm = new CatalogAdminViewModel(new AdminService(gw), new Picker());
        await vm.OnAppearingAsync();
        return (vm, gw);
    }

    static CatalogPackageRow Pkg(CatalogAdminViewModel vm, string name, bool version = false) => vm.Packages.Single(r => r.Package.Name == name && r.IsVersion == version);
    static CatalogAmenityRow Am(CatalogAdminViewModel vm, string name) => vm.Amenities.Single(r => r.Amenity.Name == name);

    static async Task AddDraftPackage(CatalogAdminViewModel vm, string name, string description = "", decimal price = 0)
    {
        vm.AddPackageCommand.Execute(null);
        (vm.FormName, vm.FormDescription, vm.FormPrice) = (name, description, price);
        await vm.SaveDraftCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Add_names_every_missing_field_and_saves_nothing()
    {
        var (vm, gw) = await Open();
        vm.AddPackageCommand.Execute(null);
        Assert.True(vm.IsPanelOpen);
        vm.FormName = "Student";
        await vm.MainCommand.ExecuteAsync(null);

        Assert.Equal("Add needs 2 more things: a description and a price. Or save it as a draft and finish later.", vm.MissingText);
        Assert.True(vm.DescriptionMissing && vm.PriceMissing);
        Assert.False(vm.NameMissing);
        Assert.True(vm.IsPanelOpen);
        Assert.DoesNotContain(await gw.ListAsync<MembershipPackage>(), p => p.Name == "Student");
    }

    [Fact]
    public async Task Save_as_draft_keeps_it_off_sale_and_add_to_sale_publishes_it()
    {
        var (vm, _) = await Open();
        await AddDraftPackage(vm, "Student");
        Assert.False(vm.IsPanelOpen);
        var row = Pkg(vm, "Student");
        Assert.Equal(("Draft", "Not set", "No description yet"), (row.StateText, row.PriceText, row.Subtitle));
        Assert.Equal(["Edit draft", "Add to sale", "Delete draft"], vm.ActionsFor(row));

        await vm.RunActionCommand.ExecuteAsync((row, "Edit draft"));
        Assert.Equal(("Edit draft", "Student"), (vm.PanelTitle, vm.FormName));
        (vm.FormDescription, vm.FormPrice) = ("Full access with a school ID.", 1000);
        vm.NewBenefit = "Gym floor and lockers";
        vm.AddBenefitCommand.Execute(null);
        await vm.MainCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        row = Pkg(vm, "Student");
        Assert.Equal(CatalogState.Published, row.Package.State);
        Assert.Equal(["Gym floor and lockers"], row.Package.Benefits);
        Assert.Equal("On sale", row.StateText);
    }

    [Fact]
    public async Task A_draft_can_be_deleted()
    {
        var (vm, gw) = await Open();
        await AddDraftPackage(vm, "Student");
        await vm.RunActionCommand.ExecuteAsync((Pkg(vm, "Student"), "Delete draft"));
        Assert.DoesNotContain(vm.Packages, r => r.Package.Name == "Student");
        Assert.DoesNotContain(await gw.ListAsync<MembershipPackage>(), p => p.Name == "Student");
    }

    [Fact]
    public async Task A_new_version_sits_under_its_package_and_is_compared_before_it_goes_on_sale()
    {
        var (vm, gw) = await Open();
        var monthly = Pkg(vm, "Monthly");
        Assert.Equal(["Create new version", "Archive"], vm.ActionsFor(monthly));

        await vm.RunActionCommand.ExecuteAsync((monthly, "Create new version"));
        Assert.Equal(("New version of Monthly", "Monthly", 1500m, "Review and publish"), (vm.PanelTitle, vm.FormName, vm.FormPrice, vm.MainLabel));
        vm.FormPrice = 1650;
        await vm.SaveDraftCommand.ExecuteAsync(null);

        var index = vm.Packages.IndexOf(Pkg(vm, "Monthly"));
        var version = vm.Packages[index + 1];
        Assert.True(version.IsVersion);
        Assert.Equal("New version, replaces the one above when published", version.Subtitle);
        Assert.Equal(["Archive"], vm.ActionsFor(Pkg(vm, "Monthly")));   // one draft version at a time
        Assert.Equal(["Edit draft", "Review and publish", "Delete draft"], vm.ActionsFor(version));

        await vm.RunActionCommand.ExecuteAsync((version, "Review and publish"));
        Assert.True(vm.IsComparing);
        Assert.Equal([("Price", "₱1,500.00", "₱1,650.00")], vm.Changes.Select(c => (c.Field, c.Old, c.New)));
        await vm.PublishVersionCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsPanelOpen);
        var now = Assert.Single(vm.Packages, r => r.Package.Name == "Monthly");
        Assert.Equal((1650m, CatalogState.Published), (now.Package.Price, now.Package.State));
        Assert.Equal(CatalogState.Archived, (await gw.ListAsync<MembershipPackage>()).Single(p => p.Id == monthly.Package.Id).State);
    }

    [Fact]
    public async Task Archive_is_offered_only_to_a_system_admin()
    {
        var (vm, _) = await Open("subadmin@demo.io");
        Assert.False(vm.CanArchive);
        Assert.Equal(["Create new version"], vm.ActionsFor(Pkg(vm, "Monthly")));
        Assert.Equal(["Edit", "Sell per session"], vm.ActionsFor(Am(vm, "Pool")));
    }

    [Fact]
    public async Task An_amenity_is_added_per_session_with_its_photo_and_edited_after_it_is_on_sale()
    {
        var (vm, _) = await Open();
        vm.AddAmenityCommand.Execute(null);
        Assert.False(vm.IsPackagePanel);
        (vm.FormName, vm.FormDescription, vm.FormPrice, vm.SoldAsPass) = ("Ice bath", "Cold plunge, 5 minutes.", 250, false);
        await vm.PickPhotoCommand.ExecuteAsync(null);
        Assert.Equal("ring.png, saved with the amenity", vm.PhotoText);
        await vm.MainCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
        var row = Am(vm, "Ice bath");
        Assert.Equal((CatalogState.Published, (int?)null, "₱250.00 per session"), (row.Amenity.State, row.Amenity.DurationDays, row.SoldAsText));
        Assert.NotNull(row.Amenity.ImagePath);
        Assert.NotNull(row.PhotoUrl);
        Assert.Equal(["Edit", "Archive"], vm.ActionsFor(row));   // already per session

        await vm.RunActionCommand.ExecuteAsync((row, "Edit"));
        Assert.Equal(("Save changes", false), (vm.MainLabel, vm.CanSaveDraft));
        vm.FormPrice = 300;
        await vm.MainCommand.ExecuteAsync(null);
        Assert.Equal(300, Am(vm, "Ice bath").Amenity.Price);
    }
}
