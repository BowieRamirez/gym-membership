using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class ReportDamagePage : BasePage
{
	public ReportDamagePage(ReportDamageViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
