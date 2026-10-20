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
	Action? _dismissMenu;

	protected override void OnDisappearing()
	{
		_dismissMenu?.Invoke();
		base.OnDisappearing();
	}

	async void OnAccountActions(object? sender, EventArgs e)
	{
		if (_actionRunning || sender is not Button { BindingContext: UserRow user } button || BindingContext is not UsersViewModel vm) return;
		_actionRunning = true;
		try
		{
			var actions = new List<string> { user.CoachActionText, user.EmployeeActionText, "Reset password", user.ActiveActionText };
			if (vm.CanDeleteAccounts) actions.Add("Delete Account");
			var action = await ShowAccountMenuAsync(button, user.Username, actions);
			_dismissMenu = null;
			if (action == user.CoachActionText) await vm.ToggleRoleCommand.ExecuteAsync($"{user.UserId}|coach");
			else if (action == user.EmployeeActionText) await vm.ToggleRoleCommand.ExecuteAsync($"{user.UserId}|employee");
			else if (action == "Reset password") vm.StartResetCommand.Execute(user);
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
		finally { _actionRunning = false; _dismissMenu = null; }
	}

	Task<string?> ShowAccountMenuAsync(Button button, string name, IReadOnlyList<string> actions)
	{
#if WINDOWS
		if (button.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement anchor)
		{
			var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
			var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
			foreach (var action in actions)
			{
				var item = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = action };
				item.Click += (_, _) => result.TrySetResult(action);
				menu.Items.Add(item);
			}
			menu.Closed += (_, _) => result.TrySetResult(null);
			_dismissMenu = menu.Hide;
			menu.ShowAt(anchor);
			return result.Task;
		}
#elif ANDROID
		if (button.Handler?.PlatformView is Android.Views.View anchor)
		{
			var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
			var menu = new Android.Widget.PopupMenu(anchor.Context!, anchor, Android.Views.GravityFlags.End);
			for (var i = 0; i < actions.Count; i++) menu.Menu?.Add(0, i, i, new Java.Lang.String(actions[i]));
			menu.MenuItemClick += (_, e) =>
			{
				e.Handled = true;
				if (e.Item is { } item) result.TrySetResult(actions[item.ItemId]);
			};
			menu.DismissEvent += (_, _) => { result.TrySetResult(null); menu.Dispose(); };
			_dismissMenu = menu.Dismiss;
			menu.Show();
			return result.Task;
		}
#endif
		return DisplayActionSheetAsync(name, "No, Cancel", null, actions.ToArray());
	}
}
