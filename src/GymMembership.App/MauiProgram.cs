using CommunityToolkit.Maui;
using GymMembership.App.Services;
using GymMembership.App.Views;
using GymMembership.Core.Backend;
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Microsoft.Extensions.Logging;

namespace GymMembership.App;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.UseMauiCommunityToolkit()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("Barlow-Regular.ttf", "Barlow");
				fonts.AddFont("Barlow-Medium.ttf", "BarlowMedium");
				fonts.AddFont("Barlow-SemiBold.ttf", "BarlowSemiBold");
				fonts.AddFont("BarlowCondensed-SemiBold.ttf", "BarlowCondensedSemiBold");
			});

		var s = builder.Services;

		// Backend seam: the real Supabase project when Resources/Raw/supabase.json is filled in, otherwise the in-memory demo.
		s.AddSingleton<ISessionStore, SecureSessionStore>();
		s.AddSingleton<ISupabaseGateway>(sp => SupabaseConfig.Load() is { } settings
			? new SupabaseGateway(settings, sp.GetRequiredService<ISessionStore>())
			: new DemoGateway());

		s.AddSingleton<IAppNavigator, AppNavigator>();
		s.AddSingleton<IProofPicker, MauiProofPicker>();
		s.AddSingleton<IAuthService, AuthService>();
		s.AddSingleton<IMembershipService, MembershipService>();
		s.AddSingleton<IPaymentStaffService, PaymentStaffService>();
		s.AddSingleton<IAmenityService, AmenityService>();
		s.AddSingleton<ICoachService, CoachService>();
		s.AddSingleton<ISessionService, SessionService>();
		s.AddSingleton<IAttendanceService, AttendanceService>();
		s.AddSingleton<INotificationService, NotificationService>();
		s.AddSingleton<IChatService, ChatService>();
		s.AddSingleton<IAdminService, AdminService>();
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
}
