using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

/// <summary>Loads the page's ViewModel each time the page appears.</summary>
public class BasePage : ContentPage
{
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (BindingContext is LoadableViewModel vm) await vm.OnAppearingAsync();
    }
}
