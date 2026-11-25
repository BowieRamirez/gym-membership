namespace GymMembership.App.Controls;

/// <summary>Owner, 2026-11-25: a DatePicker's calendar shows the gym's closed days crossed out and can't pick them
/// (Windows' CalendarDatePicker blackout days). Elsewhere it does nothing; the form's sentence and the server still refuse.</summary>
public static class ClosedDays
{
    public static readonly BindableProperty DaysProperty = BindableProperty.CreateAttached(
        "Days", typeof(IEnumerable<DateOnly>), typeof(ClosedDays), null, propertyChanged: OnDaysChanged);

    public static IEnumerable<DateOnly>? GetDays(BindableObject view) => (IEnumerable<DateOnly>?)view.GetValue(DaysProperty);
    public static void SetDays(BindableObject view, IEnumerable<DateOnly>? days) => view.SetValue(DaysProperty, days);

    static void OnDaysChanged(BindableObject view, object? oldDays, object? newDays)
    {
#if WINDOWS
        if (view is not DatePicker picker) return;
        if (oldDays is null) picker.HandlerChanged += (_, _) => Hook(picker);
        Hook(picker);
#endif
    }

#if WINDOWS
    static void Hook(DatePicker picker)
    {
        if (picker.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.CalendarDatePicker calendar || Equals(calendar.Tag, nameof(ClosedDays))) return;
        calendar.Tag = nameof(ClosedDays);
        calendar.CalendarViewDayItemChanging += (_, e) =>
            e.Item.IsBlackout = GetDays(picker)?.Contains(DateOnly.FromDateTime(e.Item.Date.Date)) == true;
    }
#endif
}
