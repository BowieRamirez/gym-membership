using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CounterPage : BasePage
{
	public CounterPage(CounterViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}
}
