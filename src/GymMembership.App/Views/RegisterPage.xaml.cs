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
		OnFieldEdited(sender, e);
	}

	// a field says what is wrong once you leave it, and the message goes as soon as it is fixed
	void OnFieldDone(object? sender, FocusEventArgs e) => Check(sender as Entry);

	void OnFieldEdited(object? sender, TextChangedEventArgs e)
	{
		if (Problem(sender as Entry).Label is { IsVisible: true }) Check(sender as Entry);
	}

	void Check(Entry? entry)
	{
		var (label, message) = Problem(entry);
		if (label is null) return;
		label.Text = message;
		label.IsVisible = message is not null && !string.IsNullOrEmpty(entry!.Text);   // an untouched empty field is not an error yet
	}

	(Label? Label, string? Message) Problem(Entry? entry)
	{
		var text = entry?.Text?.Trim() ?? "";
		if (entry == FirstNameBox) return (FirstNameError, AccountRules.IsName(text) ? null : "Use 2 to 60 letters.");
		if (entry == LastNameBox) return (LastNameError, AccountRules.IsName(text) ? null : "Use 2 to 60 letters.");
		if (entry == EmailBox) return (EmailError, AccountRules.IsEmail(text) ? null : "Enter a valid email, like name@example.com.");
		if (entry == ConfirmPasswordBox) return (ConfirmPasswordError, entry.Text == PasswordBox.Text ? null : "The passwords don't match.");
		return (null, null);
	}
}
