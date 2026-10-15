using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class MyMembershipPage : BasePage
{
	public MyMembershipPage(MyMembershipViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnRenew(object? sender, EventArgs e) => Row<MyMembershipViewModel>(sender, v => v.RenewCommand);
	void OnPayAgain(object? sender, EventArgs e) => Row<MyMembershipViewModel>(sender, v => v.PayAgainCommand);
}
