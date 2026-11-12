namespace GymMembership.App.Controls;

/// <summary>
/// The "Actions" menu of a table row (Users and roles first, then Discounts): a menu opened at the row's button, as each platform
/// draws one, or an action sheet where there is no anchor. Each page keeps one and calls <see cref="Dismiss"/> when it disappears.
/// </summary>
public sealed class ActionsMenu
{
	Action? _dismiss;

	public void Dismiss() { _dismiss?.Invoke(); _dismiss = null; }

	/// <returns>The chosen action, or null when the menu is closed without one.</returns>
	public async Task<string?> ShowAsync(Page page, Button button, string title, IReadOnlyList<string> actions)
	{
		try { return await Open(page, button, title, actions); }
		finally { _dismiss = null; }
	}

	Task<string?> Open(Page page, Button button, string title, IReadOnlyList<string> actions)
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
			_dismiss = menu.Hide;
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
			_dismiss = menu.Dismiss;
			menu.Show();
			return result.Task;
		}
#endif
		return page.DisplayActionSheetAsync(title, "No, Cancel", null, actions.ToArray());
	}
}
