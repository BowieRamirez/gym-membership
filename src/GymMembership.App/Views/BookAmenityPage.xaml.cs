using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class BookAmenityPage : BasePage
{
	public BookAmenityPage(BookAmenityViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// other members' bookings and cancellations show up without reopening the page
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(30);

	void OnCell(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.ToggleCommand);
	void OnCancel(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.CancelCommand);
	void OnEnd(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.EndCommand);
}
