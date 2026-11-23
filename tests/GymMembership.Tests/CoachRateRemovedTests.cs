using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class CoachRateRemovedTests
{
    [Fact]
    public async Task Coach_profile_saves_without_rate_and_amenity_booking_costs_amenity_price_only()
    {
        Confirm.Answer = (_, _, _) => Task.FromResult(true);
        var gw = new DemoGateway();
        await gw.SignInAsync("coach@demo.io", DemoGateway.DemoPassword);
        var coachVm = new CoachProfileViewModel(new CoachService(gw));
        await coachVm.OnAppearingAsync();
        coachVm.Bio = "Updated bio";
        coachVm.Specialty = "Boxing";
        await coachVm.SaveCommand.ExecuteAsync(null);
        Assert.Null(coachVm.ErrorMessage);
        Assert.Equal("Profile saved.", coachVm.Notice);

        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var resSvc = new ReservationService(gw);
        var tomorrow = DateTime.Today.AddDays(1);
        var start = new DateTime(tomorrow.Year, tomorrow.Month, tomorrow.Day, 10, 0, 0, DateTimeKind.Utc).AddHours(-8);
        var group = (await gw.ListAsync<Amenity>(a => a.Name == "Group classes")).Single();
        // Even if an old caller passes a coachId, no coach is attached and only amenity price is charged
        var made = (await resSvc.BookAsync(group.Id, start, 60, PayMethod.Qr, "u-coach1", null)).Value!;
        Assert.Null(made.CoachId);
        Assert.Equal(500m, made.TotalPrice);
    }
}
