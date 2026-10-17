namespace GymMembership.App;

public partial class CoachShell : Shell
{
	public CoachShell(IServiceProvider sp)
	{
		InitializeComponent();
#if WINDOWS
		SetupCoachOverview(sp);
#endif
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
	}
}
