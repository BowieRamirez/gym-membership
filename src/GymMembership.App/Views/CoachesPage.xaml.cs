using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CoachesPage : BasePage
{
	public CoachesPage(CoachesViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	// These buttons sit inside the list. Binding their Command through RelativeSource made MAUI re-evaluate the binding while a card was
	// being swapped out, threw a NullReferenceException inside the generated code, and stopped the debugger on every hire.
	static CoachCard? CardOf(object? sender) => (sender as BindableObject)?.BindingContext as CoachCard;
	void OnHire(object? sender, EventArgs e) { if (CardOf(sender) is { } card) ((CoachesViewModel)BindingContext).HireCommand.Execute(card); }
	void OnMessage(object? sender, EventArgs e) { if (CardOf(sender) is { } card) ((CoachesViewModel)BindingContext).MessageCommand.Execute(card); }
	void OnEndHire(object? sender, EventArgs e) { if (CardOf(sender) is { } card) ((CoachesViewModel)BindingContext).EndHireCommand.Execute(card); }
}
