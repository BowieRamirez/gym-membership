using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class AccountDeletionViewModel(IAccountArchiveService service, UserRow user) : LoadableViewModel
{
    public UserRow User { get; } = user;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private string reason = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConfirmCommand))] private bool deleted;
    public bool CanConfirm => !IsBusy && !Deleted && !string.IsNullOrWhiteSpace(Reason) && Reason.Trim().Length <= 500;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy)) ConfirmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm) return;
        if (await RunAsync(() => service.DeleteAsync(User.UserId, Reason))) Deleted = true;
    }
}
