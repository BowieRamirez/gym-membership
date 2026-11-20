using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CashierPage : BasePage
{
	public CashierPage(CashierViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(5);   // only a payment waiting for Xendit is checked

	void OnPickItem(object? sender, TappedEventArgs e) => Row<CashierViewModel>(sender, v => v.PickItemCommand);
	void OnPickMember(object? sender, EventArgs e) => Row<CashierViewModel>(sender, v => v.PickMemberCommand);
	void OnPickCashierSuggestion(object? sender, TappedEventArgs e) => Row<CashierViewModel>(sender, v => v.ChooseSuggestionCommand);
	void OnCodeUnfocused(object? sender, FocusEventArgs e) { if (BindingContext is CashierViewModel vm) _ = vm.CheckCodeNowAsync(); }
}
