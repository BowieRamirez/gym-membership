#if WINDOWS
using GymMembership.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GymMembership.App;

public partial class CoachShell
{
    void SetupCoachOverview(IServiceProvider services)
    {
        var overview = new FlyoutItem
        {
            Title = "Overview", Route = "overview", ClassId = "coach-overview",
            Items = { new ShellContent { Title = "Overview", ContentTemplate = new DataTemplate(() => services.GetRequiredService<CoachOverviewPage>()) } }
        };
        Items.Insert(0, overview);
        CurrentItem = overview;
    }
}
#endif
