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
    List<RefundCard> _all = [];
    public ObservableCollection<RefundCard> Requests { get; } = new();
    public string[] ItemTypes { get; } = ["All items", "Membership", "Day pass", "Amenity", "Coach add-on"];
    /// <summary>The request open in the side panel; each reload swaps in its fresh row, so the panel never shows a stale status.</summary>
    [ObservableProperty] private RefundCard? selected;
    [ObservableProperty] private string statusFilter = "All";
    [ObservableProperty] private int itemTypeIndex;
    [ObservableProperty] private string search = "";
    public string EmptyText => _all.Count == 0 ? "No cancellation or refund requests." : "No matching refund requests.";
    [RelayCommand] private void SetStatusFilter(string status) { StatusFilter = status; ApplyFilters(); }
    partial void OnItemTypeIndexChanged(int value) => ApplyFilters();
    partial void OnSearchChanged(string value) => ApplyFilters();
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
        try { await RunAsync(subscription.RefundsAsync, rows => { _all = rows.ToList(); ApplyFilters(); Selected = _all.FirstOrDefault(r => r.Request.Id == Selected?.Request.Id); }); }
        finally { _loading=false; }
    }
    void ApplyFilters()
    {
        var q = Search.Trim();
        var filtered = _all.Where(r =>
            (StatusFilter == "All" || r.Status == StatusFilter)
            && ItemTypeIndex switch
            {
                1 => r.Request.Kind == "membership",
                2 => r.Request.Kind == "day_pass",
                3 => r.Request.ReservationId is not null || r.Request.Kind is "cancel" or "no_show" or "walk_in_booking",
                4 => r.Request.Kind is "coach_addon" or "coach_access",   // 0150
                _ => true
            }
            && (q.Length == 0 || r.Member.Contains(q, StringComparison.OrdinalIgnoreCase) || r.PackageName.Contains(q, StringComparison.OrdinalIgnoreCase)));
        Fill(Requests, filtered);
        OnPropertyChanged(nameof(EmptyText));
    }
    public async Task DecideAsync(RefundCard row, bool approve, string reason)
    {
        if (IsBusy) return;
        Notice=null;
        var walkIn=row.Request.IsWalkInRequest;
        if (!await RunAsync(() => walkIn?subscription.DecideWalkInAsync(row.Request.Id,approve,reason):subscription.DecideAsync(row.Request.Id,approve,reason))) return;
        Notice=walkIn?(approve?"Refund approved. Pay the customer back, then record it here.":"Request rejected. Nothing changed for the customer.")
            :approve?"Selected term cancelled. Refund awaits manual repayment — no transfer was sent.":"Request rejected. The term is unchanged.";
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
