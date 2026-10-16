using GymMembership.Core.ViewModels;

namespace GymMembership.App.Views;

public partial class CatalogAdminPage : BasePage
{
	public CatalogAdminPage(CatalogAdminViewModel vm)
	{
		InitializeComponent();
		BindingContext = vm;
	}

	void OnEditPackage(object? sender, EventArgs e) => Row<CatalogAdminViewModel>(sender, v => v.EditPackageCommand);
	void OnTogglePackage(object? sender, EventArgs e) => Row<CatalogAdminViewModel>(sender, v => v.TogglePackageCommand);
	void OnDeletePackage(object? sender, EventArgs e) => Row<CatalogAdminViewModel>(sender, v => v.DeletePackageCommand);
	void OnToggleAmenity(object? sender, EventArgs e) => Row<CatalogAdminViewModel>(sender, v => v.ToggleAmenityCommand);
	void OnUploadAmenityPhoto(object? sender, EventArgs e) => Row<CatalogAdminViewModel>(sender, v => v.UploadAmenityPhotoCommand);
}
