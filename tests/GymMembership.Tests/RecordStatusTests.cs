using GymMembership.Core.Models;
using GymMembership.Core.Services;

// Item 15, D13: accounts, packages, amenities and codes say Active, Inactive or Deleted, in words
public class RecordStatusTests
{
    [Fact]
    public void Master_data_gets_one_of_three_words()
    {
        Assert.Equal(("Active", "Inactive", "Deleted", "Deleted"), (RecordStatus.Of(true), RecordStatus.Of(false), RecordStatus.Of(false, true), RecordStatus.Of(true, true)));
        Assert.Equal(("Active", "Inactive"), (new UserRow("u", "A", true, []).ActiveStatus, new UserRow("u", "A", false, []).ActiveStatus));
        Assert.Equal(new[] { "Active", "Inactive", "Inactive" }, new[] { "active", "expired", "inactive" }.Select(s => new DiscountRow(new Discount(), s).StatusText));
        Assert.Equal(new[] { "Deleted", "Inactive", "Inactive" }, new[] { "deleted", "inactive", "expired" }.Select(s => new ArchiveRecord { Status = s }.StatusText));
    }

    [Fact]
    public void Every_master_data_status_chip_uses_the_shared_word()
    {
        string View(params string[] path) => File.ReadAllText(Path.Combine([TestPaths.AppFolder(), .. path]));
        Assert.Contains("Status=\"{Binding ActiveStatus}\"", View("Views", "UsersPage.xaml"));
        // packages and amenities (G6, Phase 4): Draft or On sale, from the row; archived ones are on the Archive page
        var catalog = View("Views", "CatalogAdminPage.xaml");
        Assert.Equal(2, catalog.Split("<c:StatusChip").Length - 1);
        Assert.Equal(2, catalog.Split("Status=\"{Binding StateText}\"").Length - 1);
        Assert.Contains("Status=\"{Binding StatusText}\"", View("Views", "DiscountsPage.xaml"));
        Assert.Contains("Status=\"{Binding StatusText}\"", View("Controls", "ArchivePanel.xaml"));
    }
}
