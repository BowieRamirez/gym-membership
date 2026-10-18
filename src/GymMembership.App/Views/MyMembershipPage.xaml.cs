using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class MyMembershipPage : BasePage
{
    public MyMembershipPage(MyMembershipViewModel vm)
    {
        InitializeComponent();
        BindingContext=vm;
    }
    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);
    void OnPayAgain(object? sender, EventArgs e) => Row<MyMembershipViewModel>(sender,v=>v.PayAgainCommand);
    async void OnCancelPayment(object? sender, EventArgs e)
    {
        if (BindingContext is not MyMembershipViewModel vm || vm.IsBusy || vm.DialogOpen) return;
        var item = (sender as BindableObject)?.BindingContext as PaymentItem ?? vm.PayNow;
        if (item is not { CanCancel: true }) return;
        vm.DialogOpen = true;
        try
        {
            var sent = item.HasProof ? "\n\nYou already sent proof. If you really paid, talk to the front desk first: cancelling here does not return money you sent." : "";
            if (!await DisplayAlertAsync("Cancel this payment?", $"{item.What}\n₱{item.Amount:N2}\n\nNo membership will be started. You can choose a package again afterwards.{sent}", "Cancel payment", "Keep it")) return;
            await vm.CancelPaymentAsync(item);
        }
        finally { vm.DialogOpen = false; }
    }
    async void OnChoosePackage(object? sender, EventArgs e) => await Controls.PhoneNavigation.GoToAsync(Shell.Current, "//packages");
    async void OnRequestRefund(object? sender, EventArgs e)
    {
        if (BindingContext is not MyMembershipViewModel vm || vm.IsBusy || vm.DialogOpen
            || (sender as BindableObject)?.BindingContext is not TermCard card || !card.CanRequestRefund) return;
        vm.DialogOpen=true;
        try
        {
            var reason=await DisplayPromptAsync("Cancel & request refund","Why are you requesting cancellation?",maxLength:1000);
            if(string.IsNullOrWhiteSpace(reason)) return;
            if(!await DisplayAlertAsync("Submit cancellation request?",
                $"{card.Term.PackageName}\n{card.Dates}\nRefund requested: ₱{card.Term.AmountPaid:N2} (what you actually paid).\n\nYour access stays unchanged until an admin approves. Approval cancels only this term. Other paid dates stay fixed; a gap may remain. Money is repaid manually, not automatically.","Submit request","Keep term")) return;
            await vm.RequestRefundAsync(card,reason);
        }
        finally {vm.DialogOpen=false;}
    }
}
