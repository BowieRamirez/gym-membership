namespace GymMembership.App;

public partial class CoachShell : Shell
{
	public CoachShell(IServiceProvider sp)
	{
		InitializeComponent();
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
	}
}
