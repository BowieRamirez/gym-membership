namespace GymMembership.App.Views;

/// <summary>Shown in place of a members-only page until the member has an active plan.</summary>
public class LockedPage : ContentPage
{
	public LockedPage(string feature, bool paymentPending)
	{
		Title = feature;
		var action = new Button { Text = paymentPending ? "See my payment" : "See packages", HorizontalOptions = LayoutOptions.Start };
		action.Clicked += async (_, _) => await Controls.PhoneNavigation.GoToAsync(Shell.Current, paymentPending ? "//memberships" : "//packages");

		var res = Application.Current!.Resources;
		// one card with the lock and what to do next
		Content = new Border
		{
			Style = (Style)res["Panel"], Margin = new Thickness(20, 24), MaximumWidthRequest = 560, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start,
			Content = new VerticalStackLayout
			{
			Spacing = 14,
			Children =
			{
				new Controls.CardTitle { Icon = "id-card", Text = $"{feature} is for members" },
				new Label
				{
					Style = (Style)res["Muted"],
					Text = paymentPending
						? "Your payment is waiting to be verified. This page opens as soon as the owner or front desk approves it."
						: "Get a membership to use this page. Pick a package, pay at the desk or attach your receipt, and it opens once your payment is verified."
				},
				action
			}
			}
		};
	}
}
