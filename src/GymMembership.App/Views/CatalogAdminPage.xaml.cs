using GymMembership.App.Controls;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CatalogAdminPage : BasePage
{
	readonly ActionsMenu _menu = new();
	bool _actionRunning;

	public CatalogAdminPage(CatalogAdminViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	protected override void OnDisappearing()
	{
		_menu.Dismiss();
		base.OnDisappearing();
	}

	// Phase 4: one Actions menu per row, as on Users and roles and Discounts; the view model says what this row's state allows
	async void OnRowActions(object? sender, EventArgs e)
	{
		if (_actionRunning || sender is not Button { BindingContext: { } row } button || BindingContext is not CatalogAdminViewModel vm) return;
		var title = row switch { CatalogPackageRow p => p.Package.Name, CatalogAmenityRow a => a.Amenity.Name, _ => "" };
		_actionRunning = true;
		try
		{
			if (await _menu.ShowAsync(this, button, title, vm.ActionsFor(row)) is { } action)
				await vm.RunActionCommand.ExecuteAsync((row, action));
		}
		finally { _actionRunning = false; }
	}

	void OnRemoveBenefit(object? sender, EventArgs e) => Row<CatalogAdminViewModel>(sender, v => v.RemoveBenefitCommand);
}
