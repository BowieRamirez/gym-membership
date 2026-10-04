using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CatalogAdminPage : BasePage
{
	public CatalogAdminPage(CatalogAdminViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
