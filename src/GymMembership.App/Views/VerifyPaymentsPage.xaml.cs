using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class VerifyPaymentsPage : BasePage
{
	public VerifyPaymentsPage(VerifyPaymentsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
