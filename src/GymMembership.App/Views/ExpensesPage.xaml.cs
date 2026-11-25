using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class ExpensesPage : BasePage
{
    public ExpensesPage(ExpensesViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
