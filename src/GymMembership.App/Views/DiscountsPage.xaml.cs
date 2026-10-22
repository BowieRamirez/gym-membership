using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class DiscountsPage : BasePage
{
	public DiscountsPage(DiscountsViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnChangeLimit(object? sender, EventArgs e) => Row<DiscountsViewModel>(sender, v => v.ChangeLimitCommand);
	void OnToggle(object? sender, EventArgs e) => Row<DiscountsViewModel>(sender, v => v.ToggleCommand);
	void OnDelete(object? sender, EventArgs e) => Row<DiscountsViewModel>(sender, v => v.DeleteCommand);
}
