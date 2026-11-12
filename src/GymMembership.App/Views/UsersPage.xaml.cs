using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Extensions;
using GymMembership.App.Controls;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class UsersPage : BasePage
{
	public UsersPage(UsersViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	bool _actionRunning;
	readonly ActionsMenu _menu = new();

	protected override void OnDisappearing()
	{
		_menu.Dismiss();
		base.OnDisappearing();
	}

	async void OnAccountActions(object? sender, EventArgs e)
	{
		if (_actionRunning || sender is not Button { BindingContext: UserRow user } button || BindingContext is not UsersViewModel vm) return;
		_actionRunning = true;
		try
		{
			if (!vm.CanManage(user))
			{
				vm.ErrorMessage = "Only a System admin can change front desk, Admin and System admin accounts.";
				return;
			}
			// D15: roles and deleting are the super admin's; a deactivated account can only be switched back on
			// a super admin account is fixed (0037): only its password can be reset here
			var actions = !user.IsActive ? new List<string> { user.ActiveActionText }
				: user.IsSuperAdmin ? new List<string> { "Reset password" }
				: vm.IsSuperAdmin ? new List<string> { user.CoachActionText, user.EmployeeActionText, user.SubAdminActionText, "Reset password", user.ActiveActionText }
				: new List<string> { "Reset password", user.ActiveActionText };
			if (vm.CanCorrectName(user)) actions.Add("Correct name");
			if (vm.CanDeleteAccounts && user.IsActive && !user.IsSuperAdmin) actions.Add("Delete Account");
			var action = await _menu.ShowAsync(this, button, user.Username, actions);
			if (action == user.CoachActionText) await vm.ToggleRoleCommand.ExecuteAsync($"{user.UserId}|coach");
			else if (action == user.EmployeeActionText) await vm.ToggleRoleCommand.ExecuteAsync($"{user.UserId}|employee");
			else if (action == user.SubAdminActionText) await vm.ToggleRoleCommand.ExecuteAsync($"{user.UserId}|subadmin");
			else if (action == "Reset password") vm.StartResetCommand.Execute(user);
			else if (action == "Correct name") vm.StartCorrectNameCommand.Execute(user);
			else if (action == user.ActiveActionText) await vm.ToggleActiveCommand.ExecuteAsync(user);
			else if (action == "Delete Account" && vm.DeletionFor(user) is { } deletion)
			{
				await this.ShowPopupAsync(new DeleteAccountPopup(deletion), new PopupOptions { CanBeDismissedByTappingOutsideOfPopup = false });
				if (deletion.Deleted)
				{
					await vm.LoadCommand.ExecuteAsync(null);
					vm.Notice = $"{user.Username} was deleted. Account history is preserved in Archive.";
				}
			}
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine(ex);
			await DisplayAlertAsync("Account actions", "This action could not be completed. Please try again.", "OK");
		}
		finally { _actionRunning = false; }
	}
}
