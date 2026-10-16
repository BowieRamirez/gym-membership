using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CashierPage : BasePage
{
	public CashierPage(CashierViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnPickItem(object? sender, TappedEventArgs e) => Row<CashierViewModel>(sender, v => v.PickItemCommand);
}
