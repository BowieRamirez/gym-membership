using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class BookAmenityPage : BasePage
{
	public BookAmenityPage(BookAmenityViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// statuses, payments approved at the desk and other members' holds show up without reopening the page (paused while the sheet is open)
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(30);

	void OnFollow(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.ToggleFollowCommand);
	void OnOpen(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.OpenCommand);
	void OnDay(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.PickDayCommand);
	void OnTime(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.PickTimeCommand);
	void OnLength(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.PickLengthCommand);
	void OnSendProof(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.SendProofCommand);
	void OnCancel(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.CancelCommand);
	void OnEnd(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.EndCommand);
	// the reason box sits under the list; bring it into view (on a phone the row tapped can be far below the top)
	async void OnClaim(object? sender, EventArgs e)
	{
		Row<BookAmenityViewModel>(sender, v => v.ClaimCommand);
		await Task.Yield();
		await PageScroll.ScrollToAsync(ClaimPanel, ScrollToPosition.Center, true);
	}
}
