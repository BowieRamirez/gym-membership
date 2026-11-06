using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class AuditPage : BasePage
{
	public AuditPage(AuditViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
		// the system Save dialog; the view model builds the CSV
		vm.SaveFile = async (name, text) =>
		{
			using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(text)).ToArray());
			var result = await CommunityToolkit.Maui.Storage.FileSaver.Default.SaveAsync(name, stream);
			return result.IsSuccessful ? result.FilePath : null;
		};
	}
}
