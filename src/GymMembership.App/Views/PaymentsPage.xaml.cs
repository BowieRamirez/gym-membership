using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

/// <summary>Owner fixes 2026-11-19 Q8: the member's own payments (moved from My membership).</summary>
public partial class PaymentsPage : BasePage
{
    public PaymentsPage(MyPaymentsViewModel vm) { InitializeComponent(); BindingContext = vm; }
    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);
    protected override bool LiveRecords => true;
    void OnPayAgain(object? sender, EventArgs e) => Row<MyPaymentsViewModel>(sender, v => v.PayAgainCommand);
    void OnPayAgainCodeUnfocused(object? sender, FocusEventArgs e) { if (BindingContext is MyPaymentsViewModel vm) _ = vm.CheckPayAgainCodeNowAsync(); }
    async void OnCancelPayment(object? sender, EventArgs e)
    {
        if (BindingContext is not MyPaymentsViewModel vm || vm.IsBusy || vm.DialogOpen
            || (sender as BindableObject)?.BindingContext is not PaymentItem { CanCancel: true } item) return;
        vm.DialogOpen = true;
        try
        {
            var sent = item.HasProof ? "\n\nYou already sent proof. If you really paid, talk to the front desk first: cancelling here does not return money you sent." : "";
            if (!await DisplayAlertAsync("Cancel this payment?", $"{item.What}\n₱{item.Amount:N2}\n\nNo membership will be started. You can choose a package again afterwards.{sent}", "Cancel payment", "Keep it")) return;
            await vm.CancelPaymentAsync(item);
        }
        finally { vm.DialogOpen = false; }
    }
}
