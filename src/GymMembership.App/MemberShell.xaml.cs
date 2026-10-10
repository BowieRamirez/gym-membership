using GymMembership.App.Views;
using GymMembership.Core.Services;

namespace GymMembership.App;

public partial class MemberShell : Shell
{
	readonly IServiceProvider _sp;
	readonly Access _access;

	// Guests (no active plan) see every page in the menu, but the members-only ones open a "get a membership" notice instead.
	public MemberShell(IServiceProvider sp, Access access)
	{
		InitializeComponent();
		_sp = sp; _access = access;
		if (!access.Active)
			foreach (var item in Items.Where(i => i.ClassId == "members-only"))
			{
				var title = item.Title;
				((ShellContent)item.Items[0].Items[0]).ContentTemplate = new DataTemplate(() => new LockedPage(title, access.PaymentPending));
			}
		Navigated += async (_, _) => await RecheckAsync();
	}

	// If the plan was approved (or expired) while the app was open, rebuild the menu so it matches.
	async Task RecheckAsync()
	{
		var now = await _sp.GetRequiredService<IMembershipService>().AccessAsync();
		if (now.Ok && now.Value != _access) _sp.GetRequiredService<IAppNavigator>().GoTo(AppRole.Member);
	}
}
