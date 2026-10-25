using GymMembership.Core.Models;
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

public partial class ArchivePanel : ContentView
{
    public ArchivePanel() => InitializeComponent();

    void OnView(object? sender, EventArgs e)
    {
        if (BindingContext is ArchiveViewModel vm && sender is BindableObject { BindingContext: ArchiveRecord row })
            vm.ViewCommand.Execute(row);
    }
}
