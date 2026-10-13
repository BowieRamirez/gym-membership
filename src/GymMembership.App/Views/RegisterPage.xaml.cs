using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RegisterPage : BasePage
{
	public RegisterPage(RegisterViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// letters only: anything else is dropped as it is typed or pasted
	void OnNameChanged(object? sender, TextChangedEventArgs e)
	{
		if (sender is Entry entry && e.NewTextValue is { } typed && AccountRules.KeepNameChars(typed) is var kept && kept != typed)
			entry.Text = kept;
	}
}
