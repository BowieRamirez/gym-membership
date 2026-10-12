using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class GymSettingsPage : BasePage
{
	public GymSettingsPage(GymSettingsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
