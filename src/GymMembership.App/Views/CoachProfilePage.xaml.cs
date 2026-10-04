using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachProfilePage : BasePage
{
	public CoachProfilePage(CoachProfileViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
