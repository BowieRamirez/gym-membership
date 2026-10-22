using GymMembership.Core.Services;

namespace GymMembership.App.Controls;

/// <summary>One amenity: monogram, name, live status chip, why it is unavailable and when it is back. An optional action button sits underneath.</summary>
public partial class AmenityCardView : ContentView
{
	public static readonly BindableProperty ActionTextProperty = BindableProperty.Create(nameof(ActionText), typeof(string), typeof(AmenityCardView), "",
		propertyChanged: (b, _, n) => { var v = (AmenityCardView)b; v.Action.Text = n as string; v.Action.IsVisible = !string.IsNullOrEmpty(n as string); });

	public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

	public event EventHandler? ActionClicked;
	public event EventHandler? BuyClicked;

	void OnBuy(object? sender, EventArgs e) => BuyClicked?.Invoke(this, e);

	public AmenityCardView()
	{
		InitializeComponent();
		Action.Clicked += (s, e) => ActionClicked?.Invoke(this, e);   // the sender is the card, whose BindingContext is the AmenityCard
	}

	protected override void OnBindingContextChanged()
	{
		base.OnBindingContextChanged();
		if (BindingContext is AmenityCard card) Mark.Letters = Initials.From(card.Amenity.Name);
	}
}
