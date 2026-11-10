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

    /// <summary>Back with nothing left to go back to: on Android the app goes to the background, still signed in, on the same page.
    /// Left to Android, Back finished the app's only activity, and reopening built a new window, which always starts on sign-in.</summary>
    public static bool SendToBackground()
    {
#if ANDROID
        Platform.CurrentActivity?.MoveTaskToBack(true);
        return true;
#else
        return false;
#endif
    }

    // a member menu page that can be a tab: its short tab title and its icon (phone_<icon>.png)
    static readonly Dictionary<string, (string, string, string)> MemberTabs = new()
    {
        ["My membership"] = ("My membership", "Membership", "membership"), ["Packages"] = ("Packages", "Packages", "packages"),
        ["Calendar"] = ("Calendar", "Calendar", "calendar"), ["Sessions"] = ("Sessions", "Sessions", "sessions"), ["Messages"] = ("Messages", "Messages", "messages"),
    };

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
            ? new[] { ("Dashboard", "Dashboard", "overview"), ("Sessions", "Sessions", "sessions"), ("Trainees & shifts", "Trainees", "trainees"), ("Messages", "Messages", "messages") }
            : MemberMenu.Tabs(access?.Active ?? true).Select(t => MemberTabs[t.Page]).ToArray();   // F16: the order follows the membership
        var pageTypes = new Dictionary<string, Type>
        {
            ["Amenities"] = typeof(AmenitiesPage), ["Book an amenity"] = typeof(BookAmenityPage), ["Coaches"] = typeof(CoachesPage),
            ["Time requests"] = typeof(RequestsPage),
            ["Profile & settings"] = typeof(CoachProfilePage), ["Calendar"] = typeof(MemberCalendarPage), ["Renew or change"] = typeof(PackagesPage),
            ["Settings"] = typeof(SettingsPage)
        };
        var secondary = original.Where(item => !primary.Any(p => p.Item1 == item.Title)).ToList();
        foreach (var item in secondary)
        {
            // a page with its own route (settings, packages, requests, bookings) opens by it, wherever it sits in the order
            var route = string.IsNullOrEmpty(item.Route) || item.Route.StartsWith("D_FAULT", StringComparison.Ordinal) ? item.Title : item.Route;
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
                (item.Title, string.IsNullOrEmpty(item.Route) || item.Route.StartsWith("D_FAULT", StringComparison.Ordinal) ? item.Title : item.Route)).ToArray(), OpenSecondaryAsync))
        });
        _bar.Items.Add(_more);
        shell.Items.Clear();
        shell.FlyoutBehavior = FlyoutBehavior.Disabled;
        shell.Items.Add(_bar);
        shell.CurrentItem = _bar;
        _bar.CurrentItem = _bar.Items[0];
        // Phase 9 (D23, approved mockup): a white bar (light) or near-black (dark); the current tab is a black pill with a yellow icon
        // (light) or a yellow pill with a black icon (dark); other tabs muted.
        shell.SetAppThemeColor(Shell.TabBarBackgroundColorProperty, Color.FromArgb("#FFFFFF"), Color.FromArgb("#161616"));
        shell.SetAppThemeColor(Shell.TabBarForegroundColorProperty, Color.FromArgb("#FFEC00"), Color.FromArgb("#111111"));
        shell.SetAppThemeColor(Shell.TabBarTitleColorProperty, Color.FromArgb("#111111"), Color.FromArgb("#F2F2F2"));
        shell.SetAppThemeColor(Shell.TabBarUnselectedColorProperty, Color.FromArgb("#5B5B5B"), Color.FromArgb("#A6A6A6"));
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
        var backdrop = Shell.GetBackgroundColor(_shell);
        void Visit(Android.Views.View view)
        {
            // the app bar reaches up behind the status bar edge to edge and paints the theme's black colorPrimary there; give it the header's colour
            if (view is Google.Android.Material.AppBar.AppBarLayout appBar && backdrop is not null)
                appBar.SetBackgroundColor(backdrop.ToPlatform());
            if (view is Google.Android.Material.BottomNavigation.BottomNavigationView bar)
            {
                var background = new Android.Graphics.Drawables.GradientDrawable();
                var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
                var surface = Shell.GetTabBarBackgroundColor(_shell) ?? Color.FromArgb(dark ? "#161616" : "#FFFFFF");
                var pageBackground = _shell.CurrentPage?.BackgroundColor ?? _shell.BackgroundColor ?? surface;
                background.SetColor(surface.ToPlatform());
                background.SetCornerRadius(0);
                bar.BackgroundTintList = null;
                bar.Background = background;
                if (bar.Parent is Android.Views.View parent)
                    parent.SetBackgroundColor(pageBackground.ToPlatform());
                bar.LabelVisibilityMode = Google.Android.Material.Navigation.NavigationBarView.LabelVisibilityLabeled;
                bar.ItemTextAppearanceInactive = Resource.Style.TabLabel;
                bar.ItemTextAppearanceActive = Resource.Style.TabLabel_Active;
                bar.ItemHorizontalTranslationEnabled = false;
                // MAUI's MaterialComponents theme gives the indicator no shape or size, so it is drawn only once both are set here
                var density = bar.Resources?.DisplayMetrics?.Density ?? 1f;
                bar.ItemActiveIndicatorEnabled = true;
                bar.ItemActiveIndicatorColor = Android.Content.Res.ColorStateList.ValueOf(Color.FromArgb(dark ? "#FFEC00" : "#111111").ToPlatform());
                bar.ItemActiveIndicatorShapeAppearance = Google.Android.Material.Shape.ShapeAppearanceModel.InvokeBuilder().SetAllCornerSizes(16 * density).Build();
                bar.ItemActiveIndicatorWidth = (int)(64 * density);
                bar.ItemActiveIndicatorHeight = (int)(28 * density);    // 28, not 32: the pill no longer reaches down to the tab name
                bar.ActiveIndicatorLabelPadding = (int)(6 * density);   // and a gap between the pill and the name
                bar.ItemPaddingTop = (int)(8 * density);
                bar.ItemPaddingBottom = (int)(4 * density);
                bar.RequestLayout();
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
