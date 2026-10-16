using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CashierTests
{
    static async Task<(DemoGateway Gw, PaymentStaffService Svc)> Desk()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        return (gw, new PaymentStaffService(gw));
    }

    [Fact]
    public async Task Membership_items_are_the_active_packages_with_their_price_and_a_plain_name()
    {
        var (gw, svc) = await Desk();
        var items = (await svc.ItemsAsync(PayKind.Membership, null)).Value!;
        var packages = await gw.ListAsync<MembershipPackage>(p => p.IsActive == true);
        Assert.Equal(packages.Select(p => (p.Id, p.Name, (decimal?)p.Price)), items.Select(i => (i.Id, i.Label, i.Price)));
    }

    [Fact]
    public async Task Amenity_items_are_the_active_amenities_with_their_price_and_a_plain_name()
    {
        var (gw, svc) = await Desk();
        var items = (await svc.ItemsAsync(PayKind.Amenity, null)).Value!;
        var amenities = await gw.ListAsync<Amenity>(a => a.IsActive == true);
        Assert.Equal(amenities.Select(a => (a.Id, a.Name, (decimal?)a.Price)), items.Select(i => (i.Id, i.Label, i.Price)));
    }

    [Fact]
    public async Task A_renewal_item_carries_the_price_of_its_package()
    {
        var (gw, svc) = await Desk();
        var plan = (await gw.ListAsync<UserMembershipPackage>(m => m.MemberId == "u-alex")).First();
        var price = (await gw.ListAsync<MembershipPackage>(p => p.Id == plan.MembershipPackageId)).Single().Price;
        var item = (await svc.ItemsAsync(PayKind.Renewal, "u-alex")).Value!.First(i => i.Id == plan.Id);
        Assert.Equal(price, item.Price);
        Assert.DoesNotContain("₱", item.Label);
    }
}
