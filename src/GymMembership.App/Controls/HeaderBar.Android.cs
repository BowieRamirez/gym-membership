#if ANDROID
using Android.Views;
using PopupMenu = Android.Widget.PopupMenu;
using GymMembership.App.Views;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

public partial class HeaderBar
{
    PopupMenu? _accountMenu;
    bool _accountActionRunning;

    void SetupAndroidAccountMenu(Shell shell)
    {
        if (shell is not (MemberShell or CoachShell)) return;
        SignOutButton.RemoveBinding(Button.CommandProperty);
        SignOutButton.Command = null;
        SignOutButton.Text = "⌄";
        SignOutButton.FontSize = 24;
        SignOutButton.WidthRequest = 48;
        SignOutButton.MinimumHeightRequest = 48;
        SignOutButton.Padding = 0;
        SignOutButton.AutomationId = "AccountMenuButton";
        SemanticProperties.SetDescription(SignOutButton, "Account menu. Settings and Sign out.");
        SignOutButton.Clicked += (_, _) => ShowAccountMenu(shell);
        shell.Unloaded += (_, _) => _accountMenu?.Dismiss();
    }

    void ShowAccountMenu(Shell shell)
    {
        if (_accountMenu is not null || _accountActionRunning ||
            SignOutButton.Handler?.PlatformView is not Android.Views.View anchor) return;
        var menu = new PopupMenu(anchor.Context!, anchor, GravityFlags.End);
        if (menu.Menu is not { } items)
        {
            menu.Dispose();
            return;
        }
        items.Add(0, 1, 0, "Settings");
        items.Add(0, 2, 1, "Sign out");
        _accountMenu = menu;
        menu.DismissEvent += (_, _) =>
        {
            _accountMenu = null;
            menu.Dispose();
        };
        menu.MenuItemClick += async (_, e) =>
        {
            e.Handled = true;
            if (_accountActionRunning) return;
            _accountActionRunning = true;
            try
            {
                if (e.Item?.ItemId == 1)
                {
                    if (shell.CurrentPage is not SettingsPage) await shell.GoToAsync("header-settings");
                }
                else if (e.Item?.ItemId == 2 && BindingContext is HeaderViewModel vm && vm.SignOutCommand.CanExecute(null))
                    await vm.SignOutCommand.ExecuteAsync(null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                await shell.DisplayAlertAsync("Account", "This action could not be completed. Please try again.", "OK");
            }
            finally { _accountActionRunning = false; }
        };
        menu.Show();
    }
}
#endif
