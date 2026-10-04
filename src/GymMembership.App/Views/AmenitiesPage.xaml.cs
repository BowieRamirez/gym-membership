using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AmenitiesPage : BasePage
{
	public AmenitiesPage(AmenitiesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
