namespace GymMembership.App.Views;

/// <summary>Shown in place of a members-only page until the member has an active plan.</summary>
public class LockedPage : ContentPage
{
	public LockedPage(string feature, bool paymentPending)
	{
		Title = feature;
		var action = new Button { Text = paymentPending ? "See my payment" : "See packages", HorizontalOptions = LayoutOptions.Start };
		action.Clicked += async (_, _) => await Shell.Current.GoToAsync(paymentPending ? "//memberships" : "//packages");

		var res = Application.Current!.Resources;
		Content = new VerticalStackLayout
		{
			Padding = new Thickness(20, 24), Spacing = 14, MaximumWidthRequest = 520, HorizontalOptions = LayoutOptions.Start,
			Children =
			{
				new Label { Text = $"{feature} is for members", Style = (Style)res["Heading"] },
				new Label
				{
					Style = (Style)res["Muted"],
					Text = paymentPending
						? "Your payment is waiting to be verified. This page opens as soon as the owner or front desk approves it."
						: "Get a membership to use this page. Pick a package, pay at the desk or attach your receipt, and it opens once your payment is verified."
				},
				action
			}
		};
	}
}
