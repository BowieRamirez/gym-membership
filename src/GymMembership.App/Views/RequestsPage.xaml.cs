using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class RequestsPage : BasePage
{
	public RequestsPage(RequestsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
