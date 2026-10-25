using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class BookAmenityPage : BasePage
{
	readonly BookAmenityViewModel _vm;

	public BookAmenityPage(BookAmenityViewModel vm)
	{
		InitializeComponent();
		BindingContext = _vm = vm;
		vm.Amenities.CollectionChanged += (_, _) => FitGrid();
		vm.Rows.CollectionChanged += (_, _) => FitGrid();
		SizeChanged += (_, _) => FitGrid();
	}

	void OnGridAreaSized(object? sender, EventArgs e) => FitGrid();

	// Windows only: stretch the cells so the day fills the space beside Your bookings, and follow the window as it is resized.
	// Phones keep the compact sizes from the XAML and scroll sideways instead.
	void FitGrid()
	{
		if (DeviceInfo.Idiom != DeviceIdiom.Desktop || _vm.Amenities.Count == 0 || GridArea.Width <= 0) return;
		const double time = 40, gap = 10, pair = 2;   // hour label, space between columns, space between :00 and :30
		var n = _vm.Amenities.Count;
		var width = Math.Clamp((GridArea.Width - time - gap * n - pair * n) / (2 * n), 46, 260);
		// rows share the height left under the page header, controls and column titles
		var rows = Math.Max(_vm.Rows.Count, 1);
		var height = Math.Clamp((Height - 230) / rows - 3, 26, 44);
		Resources["SlotWidth"] = Math.Floor(width);
		Resources["AmenityWidth"] = Math.Floor(width) * 2 + pair;
		Resources["SlotHeight"] = Math.Floor(height);
		Resources["SlotFont"] = height >= 36 && width >= 100 ? 14d : 11d;   // bigger cells get readable labels
	}

	// other members' bookings and cancellations show up without reopening the page
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(30);

	void OnCell(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.ToggleCommand);
	void OnCancel(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.CancelCommand);
	void OnEnd(object? sender, EventArgs e) => Row<BookAmenityViewModel>(sender, v => v.EndCommand);
}
