using System.Runtime.CompilerServices;
using GymMembership.App.Views;
using GymMembership.Core.Services;
#if ANDROID
using Microsoft.Maui.Platform;
#endif

namespace GymMembership.App.Controls;

public sealed class PhoneNavigation
{
    static readonly ConditionalWeakTable<Shell, PhoneNavigation> Navigations = new();
    readonly Shell _shell;
    readonly TabBar _bar = new() { Route = "phone" };
    readonly Tab _more = new() { Title = "More", Route = "more", Icon = "phone_more.png" };
    readonly Dictionary<string, Tab> _tabs = new();
    readonly Dictionary<string, ShellContent> _secondary = new();
    readonly Dictionary<string, Page> _pages = new();
    bool _opening;
    bool _isLoaded;

    public static bool IsSupported
    {
        get
        {
#if ANDROID || IOS
            return DeviceInfo.Idiom == DeviceIdiom.Phone;
#else
            return false;
#endif
        }
    }

    public static void Attach(Shell shell, bool coach, IServiceProvider services, Access? access = null)
    {
        if (!IsSupported) return;
        var navigation = new PhoneNavigation(shell, coach, services, access);
        Navigations.Add(shell, navigation);
    }

    PhoneNavigation(Shell shell, bool coach, IServiceProvider services, Access? access)
    {
        _shell = shell;
        var original = shell.Items.ToList();
        var primary = coach
            ? new[] { ("Dashboard", "Dashboard", "overview"), ("Sessions", "Sessions", "sessions"), ("Trainees", "Trainees", "trainees"), ("Messages", "Messages", "messages") }
            : new[] { ("My membership", "Membership", "membership"), ("Packages", "Packages", "packages"), ("Sessions", "Sessions", "sessions"), ("Messages", "Messages", "messages") };
        var pageTypes = new Dictionary<string, Type>
        {
            ["Amenities"] = typeof(AmenitiesPage), ["Book an amenity"] = typeof(BookAmenityPage), ["Coaches"] = typeof(CoachesPage),
            ["Time requests"] = typeof(RequestsPage), ["My shifts"] = typeof(ShiftsPage),
            ["My coach profile"] = typeof(CoachProfilePage),
            ["Settings"] = typeof(SettingsPage)
        };
        var secondary = original.Where(item => !primary.Any(p => p.Item1 == item.Title)).ToList();
        foreach (var item in secondary)
        {
            var route = item.Title == "Settings" ? "settings" : item.Title;
            var source = item.Items[0].Items[0];
            // Locked templates are closures; ordinary pages need their registered view models.
            if (item.ClassId != "members-only" || access?.Active != false)
                source.ContentTemplate = PhonePageTemplates.ForService(services, pageTypes[item.Title]);
            _secondary.Add(route, source);
        }

        foreach (var (sourceTitle, title, icon) in primary)
        {
            var item = original.Single(item => item.Title == sourceTitle);
            var source = item.Items[0].Items[0];
            var route = sourceTitle == "My membership" ? "memberships" : icon;
            var tab = new Tab { Title = title, Route = route, Icon = $"phone_{icon}.png", ClassId = item.ClassId };
            tab.Items.Add(new ShellContent { Route = "page", ContentTemplate = source.ContentTemplate });
            _tabs.Add(route, tab);
            _bar.Items.Add(tab);
        }
        var moreItems = secondary.AsEnumerable();
#if ANDROID
        moreItems = moreItems.Where(item => item.Title != "Settings");
#endif
        _more.Items.Add(new ShellContent
        {
            Route = "menu",
            ContentTemplate = new DataTemplate(() => new PhoneMorePage(moreItems.Select(item =>
                (item.Title, item.Title == "Settings" ? "settings" : item.Title)).ToArray(), OpenSecondaryAsync))
        });
        _bar.Items.Add(_more);
        shell.Items.Clear();
        shell.FlyoutBehavior = FlyoutBehavior.Disabled;
        shell.Items.Add(_bar);
        shell.CurrentItem = _bar;
        _bar.CurrentItem = _bar.Items[0];
        // light in both themes so the icons stay black (the redesign's rule: black icons on light chips); the selected tab sits on lime
        shell.SetAppThemeColor(Shell.TabBarBackgroundColorProperty, Color.FromArgb("#FFFFFF"), Color.FromArgb("#E7EBE4"));
        shell.SetAppThemeColor(Shell.TabBarForegroundColorProperty, Color.FromArgb("#000000"), Color.FromArgb("#000000"));
        shell.SetAppThemeColor(Shell.TabBarTitleColorProperty, Color.FromArgb("#000000"), Color.FromArgb("#000000"));
        shell.SetAppThemeColor(Shell.TabBarUnselectedColorProperty, Color.FromArgb("#5A6159"), Color.FromArgb("#5A6159"));
        var app = Application.Current;
        void ThemeChanged(object? sender, AppThemeChangedEventArgs e) => shell.Dispatcher.Dispatch(StyleBar);
        shell.Loaded += (_, _) =>
        {
            _isLoaded = true;
            if (app is not null)
            {
                app.RequestedThemeChanged -= ThemeChanged;
                app.RequestedThemeChanged += ThemeChanged;
            }
            shell.Dispatcher.Dispatch(StyleBar);
        };
        shell.Unloaded += (_, _) =>
        {
            _isLoaded = false;
            if (app is not null) app.RequestedThemeChanged -= ThemeChanged;
        };
        shell.Navigated += (_, _) => shell.Dispatcher.Dispatch(StyleBar);
    }

    public static Task GoToAsync(Shell shell, string route)
    {
        if (!Navigations.TryGetValue(shell, out var navigation)) return shell.GoToAsync(route);
        var key = route.Trim('/');
        if (navigation._tabs.TryGetValue(key, out var tab))
            return shell.GoToAsync($"//phone/{tab.Route}/page");
        return navigation._secondary.ContainsKey(key) ? navigation.OpenSecondaryAsync(key) : shell.GoToAsync(route);
    }

    async Task OpenSecondaryAsync(string route)
    {
        if (_opening || !_secondary.TryGetValue(route, out var source)) return;
        _opening = true;
        try
        {
            _shell.CurrentItem = _bar;
            _bar.CurrentItem = _more;
            if (_more.Navigation.NavigationStack.Count > 1) await _more.Navigation.PopToRootAsync(false);
            if (!_pages.TryGetValue(route, out var page))
            {
                page = (Page)source.ContentTemplate.CreateContent();
                _pages.Add(route, page);
            }
            await _more.Navigation.PushAsync(page);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            await _shell.DisplayAlertAsync("Navigation", "This page could not be opened. Please try again.", "OK");
        }
        finally { _opening = false; }
    }

    void StyleBar()
    {
        if (!_isLoaded || !ReferenceEquals(_shell.Window?.Page, _shell)) return;
#if ANDROID
        if (_shell.Handler?.PlatformView is not Android.Views.View root) return;
        void Visit(Android.Views.View view)
        {
            if (view is Google.Android.Material.BottomNavigation.BottomNavigationView bar)
            {
                var background = new Android.Graphics.Drawables.GradientDrawable();
                var surface = Shell.GetTabBarBackgroundColor(_shell) ??
                    Color.FromArgb(Application.Current?.RequestedTheme == AppTheme.Dark ? "#E7EBE4" : "#FFFFFF");
                var pageBackground = _shell.CurrentPage?.BackgroundColor ?? _shell.BackgroundColor ?? surface;
                background.SetColor(surface.ToPlatform());
                background.SetCornerRadius(0);
                bar.BackgroundTintList = null;
                bar.Background = background;
                if (bar.Parent is Android.Views.View parent)
                    parent.SetBackgroundColor(pageBackground.ToPlatform());
                bar.LabelVisibilityMode = Google.Android.Material.Navigation.NavigationBarView.LabelVisibilityLabeled;
                bar.ItemHorizontalTranslationEnabled = false;
                // MAUI's MaterialComponents theme gives the indicator no shape or size, so it is drawn only once both are set here
                var density = bar.Resources?.DisplayMetrics?.Density ?? 1f;
                bar.ItemActiveIndicatorEnabled = true;
                bar.ItemActiveIndicatorColor = Android.Content.Res.ColorStateList.ValueOf(Color.FromArgb("#E4F58A").ToPlatform());
                bar.ItemActiveIndicatorShapeAppearance = Google.Android.Material.Shape.ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(16 * density).Build();
                bar.ItemActiveIndicatorWidth = (int)(64 * density);
                bar.ItemActiveIndicatorHeight = (int)(32 * density);
            }
            if (view is Android.Views.ViewGroup group)
                for (var i = 0; i < group.ChildCount; i++)
                    if (group.GetChildAt(i) is { } child) Visit(child);
        }
        Visit(root);
#elif IOS
        var platform = _shell.Handler?.PlatformView;
        var root = platform as UIKit.UIView ?? (platform as UIKit.UIViewController)?.View;
        if (root is null) return;
        void Visit(UIKit.UIView view)
        {
            if (view is UIKit.UITabBar bar)
            {
                bar.Layer.CornerRadius = 0;
                bar.ClipsToBounds = true;
            }
            foreach (var child in view.Subviews) Visit(child);
        }
        Visit(root);
#endif
    }
}
