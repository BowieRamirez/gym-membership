using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class MyMembershipViewModel
{
    bool _loading;
    public bool DialogOpen { get; set; }
    public ObservableCollection<TermCard> Terms { get; } = new();
    public override Task AutoRefreshAsync() => IsBusy || DialogOpen || _loading ? Task.CompletedTask : LoadAsync();

    public async Task CancelPaymentAsync(PaymentItem item)
    {
        if (IsBusy) return;
        Notice = null;
        if (!await RunAsync(() => subscription.CancelPendingPaymentAsync(item.Id))) return;
        Notice = "Payment cancelled. Nothing was charged; you can choose a package again.";
        await LoadAsync();
    }

    public async Task RequestRefundAsync(TermCard card, string reason, string? category = null)
    {
        if (IsBusy) return;
        Notice = null;
        if (!await RunAsync(() => subscription.RequestRefundAsync(card.Term.Id, reason, category))) return;
        Notice = "Request submitted. Your term and access stay unchanged until an admin approves. No money has been sent yet.";
        await LoadAsync();
    }
}

public partial class RefundsViewModel(ISubscriptionService subscription) : LoadableViewModel
{
    public ObservableCollection<RefundCard> Requests { get; } = new();
    /// <summary>The request open in the side panel; each reload swaps in its fresh row, so the panel never shows a stale status.</summary>
    [ObservableProperty] private RefundCard? selected;
    [RelayCommand] private void Open(RefundCard row) { Selected = row; Notice = null; }
    [RelayCommand] private void Close() => Selected = null;
    public bool DialogOpen { get; set; }
    bool _loading;
    public override Task OnAppearingAsync() => LoadAsync();
    public override Task AutoRefreshAsync() => IsBusy || DialogOpen || _loading ? Task.CompletedTask : LoadAsync();
    async Task LoadAsync()
    {
        if (_loading) return;
        _loading=true;
        try { await RunAsync(subscription.RefundsAsync, rows => { Fill(Requests,rows); Selected = Requests.FirstOrDefault(r => r.Request.Id == Selected?.Request.Id); }); }
        finally { _loading=false; }
    }
    public async Task DecideAsync(RefundCard row, bool approve, string reason)
    {
        if (IsBusy) return;
        Notice=null;
        if (!await RunAsync(() => subscription.DecideAsync(row.Request.Id,approve,reason))) return;
        Notice=approve?"Selected term cancelled. Refund awaits manual repayment — no transfer was sent.":"Request rejected. The term is unchanged.";
        await LoadAsync();
    }
    public async Task MarkPaidAsync(RefundCard row, string method, string? reference)
    {
        if (IsBusy) return;
        Notice=null;
        if (!await RunAsync(() => subscription.MarkPaidAsync(row.Request.Id,method,reference))) return;
        Notice="Manual repayment recorded.";
        await LoadAsync();
    }
}
