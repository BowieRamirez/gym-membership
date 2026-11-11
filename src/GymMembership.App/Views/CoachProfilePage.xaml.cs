using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachProfilePage : BasePage
{
	public CoachProfilePage(CoachProfileViewModel vm, SettingsPage settings)
	{
		InitializeComponent();
		BindingContext = vm;
		HostBelow(Account, settings);   // F17: one Settings page for a coach
		// owner's sketch: the coach profile sits under name and email, in the settings' main column (Theme and password beside it)
		((Layout)CoachSection.Parent).Remove(CoachSection);
		CoachSection.BindingContext = vm;
		settings.CoachSlot.Content = CoachSection;
	}
}
