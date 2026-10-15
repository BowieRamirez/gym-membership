using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AmenitiesPage : BasePage
{
	public AmenitiesPage(AmenityBoardViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(30);

	void OnFollow(object? sender, EventArgs e) => Row<AmenityBoardViewModel>(sender, v => v.ToggleFollowCommand);
}
