using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GymMembership.Core.Services;

namespace GymMembership.Core.ViewModels;

public partial class CoachPhotoViewModel(ICoachService coaches, IProfileService profile, IProofPicker picker) : LoadableViewModel
{
    [ObservableProperty] private bool isCoach;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PhotoAction))] private bool needsPhoto;
    [ObservableProperty] private string letters = "?";
    [ObservableProperty] private string? photoUrl;
    public string PhotoAction => NeedsPhoto ? "Add photo" : "Change photo";

    public override async Task OnAppearingAsync()
    {
        IsCoach = NeedsPhoto = false;
        PhotoUrl = null;
        if (!await RunAsync(coaches.MyCoachAsync, c => IsCoach = c is not null) || !IsCoach) return;
        await LoadPhotoAsync();
    }

    async Task LoadPhotoAsync()
    {
        MyProfile? current = null;
        if (!await RunAsync(profile.MeAsync, me =>
        {
            current = me;
            Letters = Initials.From(me.Name);
            NeedsPhoto = string.IsNullOrWhiteSpace(me.AvatarPath);
        })) return;
        PhotoUrl = (await profile.AvatarUrlAsync(current!.AvatarPath)).Value;
    }

    [RelayCommand]
    private async Task ChangePhotoAsync()
    {
        if (IsBusy || !IsCoach) return;
        IsBusy = true; ErrorMessage = Notice = null;
        try
        {
            if (await picker.PickAsync() is not { } file) return;
            var result = await profile.SetAvatarAsync(file.FileName, file.Data);
            if (!result.Ok) { ErrorMessage = ErrorText.For(result.Error, result.Message); return; }
            NeedsPhoto = false;
            PhotoUrl = (await profile.AvatarUrlAsync(result.Value)).Value;
            Notice = "Photo updated.";
        }
        catch (Exception ex) { ErrorMessage = ErrorText.For(ErrorMapper.Map(ex), ex.Message); }
        finally { IsBusy = false; }
    }
}
