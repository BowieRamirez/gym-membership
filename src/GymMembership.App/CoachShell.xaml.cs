namespace GymMembership.App;

public partial class CoachShell : Shell
{
	public CoachShell(IServiceProvider sp)
	{
		InitializeComponent();
		Controls.PhoneNavigation.Attach(this, coach: true, services: sp);
#if WINDOWS
		SetupCoachOverview(sp);
#endif
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
	}
}
