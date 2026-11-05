using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class SettingsPage : BasePage
{
	public SettingsPage(SettingsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
