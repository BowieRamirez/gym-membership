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
		// F16: the menu follows the membership (Core.Services.MemberMenu, the same list the phone reads)
		var byPage = Items.ToDictionary(i => i.Title);
		Items.Clear();
		foreach (var entry in MemberMenu.Order(access.Active)) { var item = byPage[entry.Page]; item.Title = entry.Title; Items.Add(item); }
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
		if (!access.Active)
			foreach (var item in Items.Where(i => i.ClassId == "members-only"))
			{
				var title = item.Title;
				((ShellContent)item.Items[0].Items[0]).ContentTemplate = new DataTemplate(() => new LockedPage(title, access.PaymentPending));
			}
		Controls.PhoneNavigation.Attach(this, coach: false, services: sp, access: access);
#if WINDOWS
		Controls.IconRail.Install(this, "Member", (Core.ViewModels.HeaderViewModel)GetTitleView(this).BindingContext, groupFrontDesk: false);
#endif
		Navigated += async (_, _) => await RecheckAsync();
	}

	protected override bool OnBackButtonPressed() => base.OnBackButtonPressed() || Controls.PhoneNavigation.SendToBackground();

	// If the plan was approved (or expired) while the app was open, rebuild the menu so it matches.
	async Task RecheckAsync()
	{
		var now = await _sp.GetRequiredService<IMembershipService>().AccessAsync();
		if (now.Ok && now.Value != _access) _sp.GetRequiredService<IAppNavigator>().GoTo(AppRole.Member);
	}
}
