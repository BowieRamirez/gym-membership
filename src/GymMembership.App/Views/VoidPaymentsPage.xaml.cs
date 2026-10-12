using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class VoidPaymentsPage : BasePage
{
	public VoidPaymentsPage(VoidPaymentsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
