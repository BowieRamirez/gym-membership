using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

/// <summary>Owner, 2026-11-16: on Windows, clicking one chip left the other yellow too (Messages Coach/Gym, Revenue period). With a real
/// mouse a WinUI Button keeps the colour it had in its pressed/hover state when its colour changes later; automation clicks never
/// showed it. Chips that show which one is chosen are ChoiceChips: the colour is on a Border behind a clear Button, so the Button's
/// own colour never changes.</summary>
public class ChoiceChipTests
{
    [Fact]
    public void Messages_knows_which_tab_is_open_as_a_yes_or_no()
    {
        var vm = new MessengerViewModel(null!, null!, null!, null!);
        Assert.Equal((true, false), (vm.IsCoachTab, vm.IsSupportTab));
        vm.Tab = "support";
        Assert.Equal((false, true), (vm.IsCoachTab, vm.IsSupportTab));
    }

    [Fact]
    public void The_chosen_chip_never_changes_a_buttons_own_colour()
    {
        string App(params string[] p) => File.ReadAllText(Path.Combine([TestPaths.AppFolder(), .. p]));
        var messages = App("Views", "MessagesPage.xaml");
        Assert.Contains("<c:ChoiceChip", messages);
        Assert.DoesNotContain("Binding=\"{Binding Tab}\"", messages);
        var revenue = App("Views", "RevenuePage.Windows.cs");
        Assert.Contains("static ChoiceChip Chip(", revenue);
        Assert.DoesNotContain("chip.BackgroundColor", revenue);
        var control = App("Controls", "ChoiceChip.cs");
        Assert.DoesNotContain("_button.BackgroundColor =", control);
        Assert.DoesNotContain("_button.TextColor =", control);
    }
}
