using CommunityToolkit.Maui.Views;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

public partial class DeleteAccountPopup : Popup
{
    public DeleteAccountPopup(AccountDeletionViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
        ConfirmButtonState(vm);
        vm.ConfirmCommand.CanExecuteChanged += OnCanExecuteChanged;
        Unloaded += (_, _) => vm.ConfirmCommand.CanExecuteChanged -= OnCanExecuteChanged;
    }

    void OnCanExecuteChanged(object? sender, EventArgs e)
    {
        if (BindingContext is AccountDeletionViewModel vm) ConfirmButtonState(vm);
    }

    void ConfirmButtonState(AccountDeletionViewModel vm) => ConfirmButton.IsEnabled = vm.ConfirmCommand.CanExecute(null);

    async void OnConfirm(object? sender, EventArgs e)
    {
        if (BindingContext is not AccountDeletionViewModel vm || !vm.ConfirmCommand.CanExecute(null)) return;
        await vm.ConfirmCommand.ExecuteAsync(null);
        if (vm.Deleted) await CloseAsync();
    }

    async void OnCancel(object? sender, EventArgs e)
    {
        if (BindingContext is AccountDeletionViewModel { IsBusy: false }) await CloseAsync();
    }
}
