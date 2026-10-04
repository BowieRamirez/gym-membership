using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachesPage : BasePage
{
	public CoachesPage(CoachesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
