using GymMembership.Core.Services;

namespace GymMembership.App.Controls;

/// <summary>
/// One amenity: its photo (if any), name, live status chip, why it is unavailable and when it is back, and who runs it.
/// An optional action chip sits underneath; Book shows when the page listens for it and the amenity can be booked (F12);
/// coaches get the "I run this" switch (F14).
/// </summary>
public partial class AmenityCardView : ContentView
{
	public static readonly BindableProperty ActionTextProperty = BindableProperty.Create(nameof(ActionText), typeof(string), typeof(AmenityCardView), "",
		propertyChanged: (b, _, n) => { var v = (AmenityCardView)b; v.Action.Text = n as string; v.Action.IsVisible = !string.IsNullOrEmpty(n as string); });

	public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

	public event EventHandler? ActionClicked;
	public event EventHandler? RunsToggled;

	EventHandler? _bookClicked;
	public event EventHandler? BookClicked { add { _bookClicked += value; ShowBook(); } remove { _bookClicked -= value; ShowBook(); } }

	void ShowBook() => Book.IsVisible = _bookClicked is not null && BindingContext is AmenityCard { CanBook: true };
	void OnBook(object? sender, EventArgs e) => _bookClicked?.Invoke(this, e);

	// the switch also fires when a refresh sets it; only a change the coach made counts
	void OnRunsToggled(object? sender, ToggledEventArgs e)
	{
		if (BindingContext is AmenityCard card && e.Value != card.RunsThis) RunsToggled?.Invoke(this, e);
	}

	protected override void OnBindingContextChanged() { base.OnBindingContextChanged(); ShowBook(); }

	public AmenityCardView()
	{
		InitializeComponent();
		Action.Clicked += (s, e) => ActionClicked?.Invoke(this, e);   // the sender is the card, whose BindingContext is the AmenityCard
	}
}
