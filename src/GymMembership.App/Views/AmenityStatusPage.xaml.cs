using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AmenityStatusPage : BasePage
{
	public AmenityStatusPage(AmenityStatusViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// the reload only refills the list of cards; it never touches the editor fields, so typing is safe
	protected override TimeSpan? AutoRefresh => TimeSpan.FromSeconds(30);

	void OnChange(object? sender, EventArgs e) => Row<AmenityStatusViewModel>(sender, v => v.SelectCommand);
	void OnMaintenance(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Maintenance");
	void OnCleaning(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Cleaning");
	void OnClosed(object? sender, EventArgs e) => ((AmenityStatusViewModel)BindingContext).QuickReasonCommand.Execute("Closed");
}
