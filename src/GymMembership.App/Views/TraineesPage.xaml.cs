using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class TraineesPage : BasePage
{
	public TraineesPage(TraineesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
