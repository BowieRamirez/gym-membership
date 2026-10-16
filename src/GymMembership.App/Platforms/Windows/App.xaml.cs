using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GymMembership.App.WinUI;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : MauiWinUIApplication
{
	/// <summary>
	/// Initializes the singleton application object.  This is the first line of authored code
	/// executed, and as such is the logical equivalent of main() or WinMain().
	/// </summary>
	public App()
	{
		// Diagnostic: write an unhandled UI exception, in full, to a file. Visual Studio stops on the exception hook before it can be read,
		// and a customer's machine has no debugger at all. The file is %TEMP%\gym-membership-crash.log.
		UnhandledException += (_, e) => LogCrash(e.Exception);
		this.InitializeComponent();
	}

	static void LogCrash(Exception? error)
	{
		try
		{
			File.AppendAllText(Path.Combine(Path.GetTempPath(), "gym-membership-crash.log"),
				$"{DateTime.Now:O}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
		}
		catch { /* a log that cannot be written must not become a second crash */ }
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

