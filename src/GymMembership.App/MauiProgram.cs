using CommunityToolkit.Maui;
using GymMembership.App.Controls;
using GymMembership.App.Services;
using GymMembership.App.Views;
using GymMembership.Core.Backend;
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace GymMembership.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.UseBarcodeReader()   // ZXing.Net.MAUI: member QR (Phase 10)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Barlow-Regular.ttf", "Barlow");
				fonts.AddFont("Barlow-Medium.ttf", "BarlowMedium");
				fonts.AddFont("Barlow-SemiBold.ttf", "BarlowSemiBold");
				fonts.AddFont("BarlowCondensed-SemiBold.ttf", "BarlowCondensedSemiBold");
			});

		// every input's Field frame lights up while it has focus
		Microsoft.Maui.Handlers.ViewHandler.ViewMapper.AppendToMapping(nameof(FieldFocus), (_, view) => { if (view is InputView input) FieldFocus.Attach(input); });
#if ANDROID
		// inputs already sit in a rounded Field frame, so drop Android's own underline (the frame shows focus instead)
		var noUnderline = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, _) => h.PlatformView.BackgroundTintList = noUnderline);
		Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, _) => h.PlatformView.BackgroundTintList = noUnderline);
		Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, _) => h.PlatformView.BackgroundTintList = noUnderline);
		Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, _) => h.PlatformView.BackgroundTintList = noUnderline);
		Microsoft.Maui.Handlers.TimePickerHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, _) => h.PlatformView.BackgroundTintList = noUnderline);
#elif WINDOWS
		// WinUI fills every input with 6 % white, which showed as a second box inside the Field frame. Overrides in
		// Platforms/Windows/App.xaml don't reach that fill, so each input gets it cleared on itself (unless a page sets a background).
		Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, v) => NoFill(h.PlatformView, v, "TextControl"));
		Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, v) => NoFill(h.PlatformView, v, "TextControl"));
		Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, v) => NoFill(h.PlatformView, v, "ComboBox"));
		Microsoft.Maui.Handlers.DatePickerHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, v) => NoFill(h.PlatformView, v, "CalendarDatePicker"));
		Microsoft.Maui.Handlers.TimePickerHandler.Mapper.AppendToMapping(nameof(IView.Background), (h, v) => NoFill(h.PlatformView, v, "TimePickerButton"));
		// a picker's Title is its prompt ("Choose a member"): inside the empty box, as on Android, not a second label above it.
		// MAUI puts it back as a header whenever the title or its colour is mapped, so both are followed.
		static void Prompt(Microsoft.Maui.Handlers.IPickerHandler h, IPicker v)
		{
			h.PlatformView.Header = null;
			h.PlatformView.HeaderTemplate = null;
			h.PlatformView.PlaceholderText = v.Title ?? "";
		}
		Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping(nameof(IPicker.Title), Prompt);
		Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping(nameof(IPicker.TitleColor), Prompt);
#endif
		// text links and the password eye: no button shape behind them. Windows' button template draws a grey pill on rest, hover and
		// press, so it is swapped for a bare one that only shows the content; Android's button lifts with a shadow, which is turned off.
		// Focus, keyboard, tap feedback and screen-reader behaviour belong to the Button and stay.
		Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping(nameof(IView.Background), (handler, view) =>
		{
			if (view is not Button b || (b.Style != Application.Current?.Resources["LinkButton"] && b.Parent is not EyeButton)) return;
#if WINDOWS
			if (handler.PlatformView.Template != BareButton) handler.PlatformView.Template = BareButton;
#elif ANDROID
			handler.PlatformView.StateListAnimator = null;
			handler.PlatformView.Elevation = 0;
#endif
		});

		var s = builder.Services;

		// Backend seam: the real Supabase project when Resources/Raw/supabase.json is filled in, otherwise the in-memory demo.
		s.AddSingleton<ISessionStore, SecureSessionStore>();
		s.AddSingleton<ISupabaseGateway>(sp => SupabaseConfig.Load() is { } settings
			? new SupabaseGateway(settings, sp.GetRequiredService<ISessionStore>())
			: new DemoGateway());

		s.AddSingleton<IAppNavigator, AppNavigator>();
		// the real photo library on a device; in demo mode a generated picture, so the screens can be tried without choosing a file
		s.AddSingleton<IProofPicker>(sp => sp.GetRequiredService<ISupabaseGateway>() is DemoGateway ? new DemoPhotoPicker() : new MauiProofPicker());
		s.AddSingleton<IReportFiles, MauiReportFiles>();
		s.AddSingleton<IAuthService, AuthService>();
		s.AddSingleton<IMembershipService, MembershipService>();
		s.AddSingleton<ISubscriptionService, SubscriptionService>();
		s.AddSingleton<IPaymentStaffService, PaymentStaffService>();
		s.AddSingleton<IAmenityService, AmenityService>();
		s.AddSingleton<IReservationService, ReservationService>();
		s.AddSingleton<ICoachService, CoachService>();
		s.AddSingleton<ISessionService, SessionService>();
		s.AddSingleton<IAttendanceService, AttendanceService>();
		s.AddSingleton<IDeskStore, FileDeskStore>();
		s.AddSingleton<OfflineDesk>();
		s.AddSingleton<INotificationService, NotificationService>();
		s.AddSingleton<IChatService, ChatService>();
		// live updates need the real project; the demo and the tests use the do-nothing version
		s.AddSingleton<IRealtimeChat>(sp => sp.GetRequiredService<ISupabaseGateway>() is SupabaseGateway g ? new SupabaseRealtimeChat(g) : NoRealtime.Instance);
		s.AddSingleton<IDeskHomeService, DeskHomeService>();
		s.AddSingleton<IAdminService, AdminService>();
		s.AddSingleton<IAccountArchiveService, AccountArchiveService>();
		s.AddSingleton<IArchiveService, ArchiveService>();
		s.AddSingleton<IProfileService, ProfileService>();
		s.AddSingleton<ISettingsService, SettingsService>();

		// every ViewModel and every page, by convention
		foreach (var vm in typeof(LoadableViewModel).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(LoadableViewModel))))
			s.AddTransient(vm);
		foreach (var page in typeof(BasePage).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(BasePage))))
			s.AddTransient(page);

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}

#if WINDOWS
	// fill and edge of a WinUI input in every state: "TextControl" + "Background" + "PointerOver", and so on
	static void NoFill(Microsoft.UI.Xaml.Controls.Control input, IView view, string prefix)
	{
		if (view.Background is not null) return;
		var none = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
		input.Background = input.BorderBrush = none;
		foreach (var part in new[] { "Background", "BorderBrush" })
			foreach (var state in new[] { "", "PointerOver", "Pressed", "Focused", "Disabled" }) input.Resources[prefix + part + state] = none;
	}

	static Microsoft.UI.Xaml.Controls.ControlTemplate? _bareButton;
	static Microsoft.UI.Xaml.Controls.ControlTemplate BareButton => _bareButton ??= (Microsoft.UI.Xaml.Controls.ControlTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
		"""
		<ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
		    <ContentPresenter Background="Transparent" Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}"
		                      Padding="{TemplateBinding Padding}" Foreground="{TemplateBinding Foreground}"
		                      HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}" />
		</ControlTemplate>
		""");
#endif
}
