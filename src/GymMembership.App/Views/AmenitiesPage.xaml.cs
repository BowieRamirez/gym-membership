using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AmenitiesPage : BasePage
{
	public AmenitiesPage(AmenitiesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnAvail(object? sender, EventArgs e) => Row<AmenitiesViewModel>(sender, v => v.AvailCommand);
	void OnLogUsage(object? sender, EventArgs e) => Row<AmenitiesViewModel>(sender, v => v.LogUsageCommand);
}
