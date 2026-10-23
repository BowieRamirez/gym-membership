using GymMembership.Core.ViewModels;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace GymMembership.App.Views;

public partial class CheckInPage : BasePage
{
	public CheckInPage(CheckInViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
		vm.PropertyChanged += async (_, e) => { if (e.PropertyName == nameof(vm.IsScanning)) await CameraAsync(vm); };
	}

	// The camera exists only while scanning, so it is never running in the background.
	async Task CameraAsync(CheckInViewModel vm)
	{
		if (!vm.IsScanning) { StopCamera(); return; }
		if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
		{ vm.ErrorMessage = "Allow camera access in your device settings to scan, or use the member search below."; vm.IsScanning = false; return; }
		var reader = new CameraBarcodeReaderView { Options = new BarcodeReaderOptions { Formats = BarcodeFormats.TwoDimensional, AutoRotate = true, Multiple = false } };
		reader.BarcodesDetected += (_, e) =>
		{
			if (e.Results.FirstOrDefault()?.Value is not { Length: > 0 } code) return;
			reader.IsDetecting = false;   // one code per scan
			MainThread.BeginInvokeOnMainThread(() => vm.ScanCommand.Execute(code));
		};
		CameraHost.Content = reader;
	}

	void StopCamera()
	{
		if (CameraHost.Content is CameraBarcodeReaderView reader) { reader.IsDetecting = false; reader.Handler?.DisconnectHandler(); }
		CameraHost.Content = null;
	}

	protected override void OnDisappearing() { base.OnDisappearing(); if (BindingContext is CheckInViewModel vm) vm.IsScanning = false; StopCamera(); }

	void OnCheckIn(object? sender, EventArgs e) => Row<CheckInViewModel>(sender, v => v.CheckInCommand);
	void OnToggleCoach(object? sender, EventArgs e) => Row<CheckInViewModel>(sender, v => v.ToggleCoachCommand);
}
