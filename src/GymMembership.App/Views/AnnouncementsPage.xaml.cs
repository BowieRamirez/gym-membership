using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AnnouncementsPage : BasePage
{
	public AnnouncementsPage(AnnouncementsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
