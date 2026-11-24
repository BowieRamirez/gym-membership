using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>0148 (owner, 2026-11-25): an unpaid online booking holds its slot for 15 minutes.</summary>
public class SlotHoldTests
{
    [Fact]
    public async Task An_unpaid_booking_holds_the_slot_from_another_member()
    {
        var gw = new DemoGateway();
        Assert.True((await new AuthService(gw).RegisterAsync("Ana", "Reyes", "ana@demo.io", "secret12", true)).Ok);
        var ana = new MembershipService(gw);
        var monthly = (await ana.ListPackagesAsync()).Value!.First(p => p.Name == "Monthly");
        await ana.PayOnlineAsync((await ana.AvailAsync(monthly.Id)).Value!.Id);
        var pool = (await gw.ListAsync<Amenity>(a => a.Name == "Pool")).Single();
        var start = DateTime.UtcNow.AddHours(8).Date.AddDays(2).AddHours(10 - 8);   // 10 am Manila, two days on

        var held = (await new ReservationService(gw).BookAsync(pool.Id, start, 60, PayMethod.Qr)).Value!;
        Assert.Equal("awaiting_payment", held.Status);
        Assert.NotNull(held.HeldUntil);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);   // Alex
        Assert.Equal("slot_taken", (await new ReservationService(gw).BookAsync(pool.Id, start, 60, PayMethod.Qr)).Message);
    }
}
