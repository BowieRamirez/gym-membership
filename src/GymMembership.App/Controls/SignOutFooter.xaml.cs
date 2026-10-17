namespace GymMembership.App.Controls;

public partial class SignOutFooter : ContentView
{
	public SignOutFooter()
	{
		InitializeComponent();
#if WINDOWS
		var button = (Button)Content;
		button.HandlerChanged += (_, _) =>
		{
			if (button.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.Button native)
				native.HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left;
		};
#endif
	}
}
