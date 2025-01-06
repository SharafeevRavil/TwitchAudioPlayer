using CommunityToolkit.Mvvm.ComponentModel;
using TwitchAudioPlayer.WPF.Services;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class VkSettingsViewModel : ModalViewModelBase
{
    private readonly IUserSettingsManager _settingsManager;

    [ObservableProperty] private long? _userId;

    public VkSettingsViewModel(IUserSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
        UserId = _settingsManager.Settings.VkUserId;
    }

    protected override async Task SaveAsync()
    {
        _settingsManager.Settings.VkUserId = UserId;
        await _settingsManager.SaveSettingsAsync();
        await base.SaveAsync();
    }
}