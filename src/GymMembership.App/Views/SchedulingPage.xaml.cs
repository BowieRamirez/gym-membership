using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class SchedulingPage : BasePage
{
    public SchedulingPage(SchedulingViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
