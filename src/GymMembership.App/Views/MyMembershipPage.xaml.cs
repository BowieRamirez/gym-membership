using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class MyMembershipPage : BasePage
{
	public MyMembershipPage(MyMembershipViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
