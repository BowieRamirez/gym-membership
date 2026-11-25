using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CashierPage : BasePage
{
	public CashierPage(CashierViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
		// 0164: the Start shift countdown, once a second while the shift isn't started
		_tick = Dispatcher.CreateTimer();
		_tick.Interval = TimeSpan.FromSeconds(1);
		_tick.Tick += (_, _) => { if (vm.NeedsShift) vm.TickShift(); };
	}

	readonly IDispatcherTimer _tick;
	protected override void OnAppearing() { base.OnAppearing(); _tick.Start(); }
	protected override void OnDisappearing() { _tick.Stop(); base.OnDisappearing(); }

	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(5);   // only a payment waiting for Xendit is checked

	void OnPickItem(object? sender, TappedEventArgs e) => Row<CashierViewModel>(sender, v => v.PickItemCommand);
	// the two booking forms (moved from Amenity status, 2026-11-23) have their own customer boxes
	void OnPickCustomer(object? sender, TappedEventArgs e)
	{
		if (BindingContext is CashierViewModel { Booking: { } b } && (sender as BindableObject)?.BindingContext is GymMembership.Core.Services.MemberHit hit) b.ChooseSuggestionCommand.Execute(hit);
	}
	void OnPickCoachCustomer(object? sender, TappedEventArgs e)
	{
		if (BindingContext is CashierViewModel { CoachBooking: { } cb } && (sender as BindableObject)?.BindingContext is GymMembership.Core.Services.MemberHit hit) cb.ChooseCoachCustomerCommand.Execute(hit);
	}
	void OnPickDayPassOption(object? sender, EventArgs e) => Row<CashierViewModel>(sender, v => v.PickDayPassOptionCommand);
	void OnPickMember(object? sender, EventArgs e) => Row<CashierViewModel>(sender, v => v.PickMemberCommand);
	void OnPickCashierSuggestion(object? sender, TappedEventArgs e) => Row<CashierViewModel>(sender, v => v.ChooseSuggestionCommand);
	void OnCodeUnfocused(object? sender, FocusEventArgs e) { if (BindingContext is CashierViewModel vm) _ = vm.CheckCodeNowAsync(); }
}
