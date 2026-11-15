using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RegisterPage : BasePage
{
	public RegisterPage(RegisterViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
		// it appears without the person leaving the field, so a screen reader has to be told
		vm.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(vm.EmailTaken) && vm.EmailTaken is { } message) SemanticScreenReader.Announce(message);
			if (e.PropertyName == nameof(vm.TermsError) && vm.TermsError is { } terms) SemanticScreenReader.Announce(terms);
			if (e.PropertyName == nameof(vm.TermsOpen)) ShowTerms(vm.TermsOpen);
		};
		_ = LoadTermsAsync();
#if WINDOWS
		HandlerChanged += (_, _) =>
		{
			if (Handler?.PlatformView is Microsoft.UI.Xaml.UIElement view)
				view.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape && vm.TermsOpen) { vm.TermsOpen = false; e.Handled = true; } };
		};
#endif
	}

	RegisterViewModel Vm => (RegisterViewModel)BindingContext;

	// Android Back closes the terms first, then leaves the page as usual
	protected override bool OnBackButtonPressed()
	{
		if (!Vm.TermsOpen) return base.OnBackButtonPressed();
		Vm.TermsOpen = false;
		return true;
	}

	void OnAgreeText(object? sender, TappedEventArgs e) => Vm.AgreedToTerms = !Vm.AgreedToTerms;

	// the form steps out of the way (and out of Tab order) while the terms are open; the brand panel dims beside a wide sheet, and goes on a narrow screen
	void ShowTerms(bool open)
	{
		Form.IsVisible = !open;
		if (this.GetVisualTreeDescendants().OfType<Controls.AuthLayout>().FirstOrDefault() is { } shell && shell.Children[0] is VisualElement brand)
		{
			var wide = shell.ColumnDefinitions.Count == 2;
			brand.Opacity = open && wide ? 0.35 : 1;
			brand.IsVisible = !open || wide;
		}
		if (open) CloseTermsButton.Focus();
	}

	// terms.md: "# " is the sheet's own title, the version lines are captions, "## " are headings, the rest are paragraphs
	async Task LoadTermsAsync()
	{
		using var stream = await FileSystem.OpenAppPackageFileAsync("terms.md");
		using var reader = new StreamReader(stream);
		while (await reader.ReadLineAsync() is { } line)
		{
			if (line.Length == 0 || line.StartsWith("# ")) continue;
			var label = line.StartsWith("## ") ? new Label { Text = line[3..], FontFamily = "BarlowSemiBold", FontSize = 17, Margin = new Thickness(0, 14, 0, 0) }
				: line.StartsWith("Version ") || line.StartsWith("Draft:") ? new Label { Text = line, Style = (Style)Application.Current!.Resources["Caption"], Margin = new Thickness(0, 0, 0, line.StartsWith("Draft:") ? 10 : 0) }
				: new Label { Text = line, LineHeight = 1.3 };
			if (line.StartsWith("## ")) SemanticProperties.SetHeadingLevel(label, SemanticHeadingLevel.Level2);
			TermsText.Add(label);
		}
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
