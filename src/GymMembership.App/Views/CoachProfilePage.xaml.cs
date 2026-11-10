using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachProfilePage : BasePage
{
	public CoachProfilePage(CoachProfileViewModel vm, SettingsPage settings)
	{
		InitializeComponent();
		BindingContext = vm;
		HostBelow(Account, settings);   // F17: one "Profile & settings" page
	}
}
