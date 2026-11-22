using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class PayrollPage : BasePage
{
    public PayrollPage(PayrollViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    void OnOpenDrawerClicked(object? sender, EventArgs e) => Row<PayrollViewModel>(sender, v => v.OpenDrawerCommand);
    void OnRemoveLineClicked(object? sender, EventArgs e) => Row<PayrollViewModel>(sender, v => v.RemoveLineCommand);

    void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is BindableObject b && b.BindingContext is PayrollRowItem row && BindingContext is PayrollViewModel vm)
            vm.OpenDrawerCommand.Execute(row);
    }
}
