using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RefundsPage : BasePage
{
    public RefundsPage(RefundsViewModel vm) {InitializeComponent();BindingContext=vm;}
    protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(20);
    async void OnApprove(object? sender, EventArgs e) => await DecideAsync(sender,true);
    async void OnReject(object? sender, EventArgs e) => await DecideAsync(sender,false);
    async Task DecideAsync(object? sender,bool approve)
    {
        if(BindingContext is not RefundsViewModel vm || vm.IsBusy || vm.DialogOpen
            || (sender as BindableObject)?.BindingContext is not RefundCard row || !row.CanDecide) return;
        vm.DialogOpen=true;
        try
        {
            var reason=await DisplayPromptAsync(approve?"Approval note":"Rejection reason","Enter the decision reason (required).",maxLength:1000);
            if(string.IsNullOrWhiteSpace(reason)) return;
            if(!await DisplayAlertAsync(approve?"Approve cancellation?":"Reject request?",
                $"{row.Member} · {row.PackageName}\n{row.Dates}\n₱{row.Request.Amount:N2}\n\n"+(approve
                    ?"Only this term stops granting access immediately. Other paid terms keep their dates. The refund will await manual repayment — no money is sent by approving."
                    :"The selected term and access will remain unchanged."),approve?"Approve":"Reject","Go back")) return;
            await vm.DecideAsync(row,approve,reason);
        }
        finally {vm.DialogOpen=false;}
    }
    async void OnPaid(object? sender,EventArgs e)
    {
        if(BindingContext is not RefundsViewModel vm || vm.IsBusy || vm.DialogOpen
            || (sender as BindableObject)?.BindingContext is not RefundCard row || !row.CanRepay) return;
        vm.DialogOpen=true;
        try
        {
            var choice=await DisplayActionSheetAsync("How did you return the money?","Go back",null,"Cash","GCash","Bank transfer");
            var method=choice switch {"Cash"=>PayMethod.Cash,"GCash"=>PayMethod.Gcash,"Bank transfer"=>PayMethod.BankTransfer,_=>null};
            if(method is null) return;
            string? reference=null;
            if(method!=PayMethod.Cash)
            {
                reference=await DisplayPromptAsync("Repayment reference","Enter the reference for the actual completed repayment.",maxLength:200);
                if(string.IsNullOrWhiteSpace(reference)) return;
            }
            if(!await DisplayAlertAsync("Confirm money was returned",
                $"I have already returned ₱{row.Request.Amount:N2} to {row.Member} via {choice}.\n{reference}\n\nThis records the repayment only. It does not transfer money. It cannot be marked paid twice.","Record repaid","Go back")) return;
            await vm.MarkPaidAsync(row,method,reference);
        }
        finally {vm.DialogOpen=false;}
    }
}
