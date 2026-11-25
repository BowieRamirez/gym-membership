using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AttendanceReviewPage : BasePage
{
    public AttendanceReviewPage(AttendanceReviewViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    void OnRemoveCorrectionClicked(object? sender, EventArgs e) => Row<AttendanceReviewViewModel>(sender, v => v.RemoveCorrectionCommand);
}
