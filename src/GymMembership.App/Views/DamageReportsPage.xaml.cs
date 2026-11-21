using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class DamageReportsPage : BasePage
{
	public DamageReportsPage(DamageReportsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnReview(object? sender, EventArgs e) => Row<DamageReportsViewModel>(sender, v => v.StartReviewCommand);
}
