using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls;

namespace GymMembership.App.Controls;

public static class PhonePageTemplates
{
    public static DataTemplate ForService(IServiceProvider services, Type pageType) =>
        new(() => services.GetRequiredService(pageType));
}
