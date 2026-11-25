using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class EmployeesPage : BasePage
{
    public EmployeesPage(EmployeesViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
