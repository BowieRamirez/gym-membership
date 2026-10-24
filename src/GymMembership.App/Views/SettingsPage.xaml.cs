using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class SettingsPage : BasePage
{
	public SettingsPage(SettingsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// letters only, as on the register page
	void OnNameChanged(object? sender, TextChangedEventArgs e)
	{
		if (sender is Entry entry && e.NewTextValue is { } typed && AccountRules.KeepNameChars(typed) is var kept && kept != typed)
			entry.Text = kept;
	}
}
