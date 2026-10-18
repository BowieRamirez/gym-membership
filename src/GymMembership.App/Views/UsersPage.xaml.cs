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

	void OnToggleRoleCoach(object? sender, EventArgs e) => Row<UsersViewModel>(sender, v => v.ToggleRoleCommand, row => $"{((UserRow)row).UserId}|coach");
	void OnToggleRoleEmployee(object? sender, EventArgs e) => Row<UsersViewModel>(sender, v => v.ToggleRoleCommand, row => $"{((UserRow)row).UserId}|employee");
	void OnStartReset(object? sender, EventArgs e) => Row<UsersViewModel>(sender, v => v.StartResetCommand);
	void OnToggleActive(object? sender, EventArgs e) => Row<UsersViewModel>(sender, v => v.ToggleActiveCommand);
}
