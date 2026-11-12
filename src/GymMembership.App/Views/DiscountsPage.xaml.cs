using GymMembership.App.Controls;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class DiscountsPage : BasePage
{
	readonly ActionsMenu _menu = new();
	bool _actionRunning;

	public DiscountsPage(DiscountsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	protected override void OnDisappearing()
	{
		_menu.Dismiss();
		base.OnDisappearing();
	}

	// G1: one Actions menu per code, as on Users and roles; the view model says which actions this code allows
	async void OnCodeActions(object? sender, EventArgs e)
	{
		if (_actionRunning || sender is not Button { BindingContext: DiscountRow row } button || BindingContext is not DiscountsViewModel vm) return;
		_actionRunning = true;
		try
		{
			if (await _menu.ShowAsync(this, button, row.Discount.Code, vm.ActionsFor(row)) is { } action)
				await vm.RunActionCommand.ExecuteAsync((row, action));
		}
		finally { _actionRunning = false; }
	}
}
