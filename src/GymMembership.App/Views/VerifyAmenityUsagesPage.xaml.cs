using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class VerifyAmenityUsagesPage : BasePage
{
	public VerifyAmenityUsagesPage(VerifyAmenityUsagesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
