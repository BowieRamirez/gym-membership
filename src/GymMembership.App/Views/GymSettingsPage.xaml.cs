using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class GymSettingsPage : BasePage
{
	public GymSettingsPage(GymSettingsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnReopen(object? sender, EventArgs e) => Row<GymSettingsViewModel>(sender, v => v.ReopenCommand);   // G16
}
