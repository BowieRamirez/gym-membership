#if WINDOWS
using GymMembership.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace GymMembership.App;

public partial class StaffShell
{
    void SetupAdminOverview(IServiceProvider services)
    {
        var overview = new FlyoutItem
        {
            Title = "Overview", Route = "overview", ClassId = "admin-overview",
            Items = { new ShellContent { Title = "Overview", ContentTemplate = new DataTemplate(() => services.GetRequiredService<AdminOverviewPage>()) } }
        };
        Items.Insert(0, overview);
        CurrentItem = overview;
    }

    public void ShowAdminPage(string title)
    {
        var item = Items.FirstOrDefault(i => i.ClassId == "admin" && i.Title == title);
        if (item is not null) CurrentItem = item;
    }
}
#endif
