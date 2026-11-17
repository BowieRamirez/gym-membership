using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class PackagesPage : BasePage
{
	public PackagesPage(PackagesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnAvail(object? sender, EventArgs e) => Row<PackagesViewModel>(sender, v => v.OpenBuyCommand);
	void OnUseCode(object? sender, EventArgs e) => Row<PackagesViewModel>(sender, v => v.UseCodeCommand);
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);   // back from Xendit's page, the next tick confirms the payment
	async void OnCancelPayment(object? sender, EventArgs e)
	{
		if (BindingContext is not PackagesViewModel vm || vm.IsBusy || vm.DialogOpen || (vm.PayNow ?? vm.PayAtDesk) is not { } item) return;
		vm.DialogOpen = true;
		try
		{
			if (!await DisplayAlertAsync("Cancel this payment?", $"{item.What}\n₱{item.Amount:N2}\n\nNo membership will be started. You can choose a package again afterwards.", "Cancel payment", "Keep it")) return;
			await vm.CancelPaymentAsync(item);
		}
		finally { vm.DialogOpen = false; }
	}
	void OnToggleRules(object? sender, EventArgs e) => Rules.IsVisible = !Rules.IsVisible;   // phones: the rules stay folded until asked for
}
