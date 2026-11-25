using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class FinancePage : BasePage
{
    public FinancePage(FinanceViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
