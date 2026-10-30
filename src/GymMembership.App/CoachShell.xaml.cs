namespace GymMembership.App;

public partial class CoachShell : Shell
{
	public CoachShell(IServiceProvider sp)
	{
		InitializeComponent();
		Controls.PhoneNavigation.Attach(this, coach: true, services: sp);
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
#if WINDOWS
		Controls.IconRail.Install(this, "Coach", (Core.ViewModels.HeaderViewModel)GetTitleView(this).BindingContext, groupFrontDesk: false);
#endif
	}
}
