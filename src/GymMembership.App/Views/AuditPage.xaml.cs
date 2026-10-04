using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AuditPage : BasePage
{
	public AuditPage(AuditViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
