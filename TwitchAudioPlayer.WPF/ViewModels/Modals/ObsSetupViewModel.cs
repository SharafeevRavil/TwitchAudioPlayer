using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.Services.Obs;

namespace TwitchAudioPlayer.WPF.ViewModels.Modals;

public partial class ObsSetupViewModel(
    IUserSettingsManager settingsManager,
    ObsSceneSetupService obsSceneSetupService) : ModalViewModelBase
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 4455;

    private Func<ObsWindowCaptureTarget>? _windowTargetFactory;
    private Func<ObsCrop>? _cropFactory;

    [ObservableProperty] private string _host = DefaultHost;
    [ObservableProperty] private int _port = DefaultPort;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private string _groupName = "TwitchAudioPlayer";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;

    public void Initialize(Func<ObsWindowCaptureTarget> windowTargetFactory, Func<ObsCrop> cropFactory)
    {
        _windowTargetFactory = windowTargetFactory;
        _cropFactory = cropFactory;

        var settings = settingsManager.Settings.ObsWebSocket;
        Host = string.IsNullOrWhiteSpace(settings.Host) ? DefaultHost : settings.Host;
        Port = settings.Port > 0 ? settings.Port : DefaultPort;
        Password = settings.Password;
        GroupName = string.IsNullOrWhiteSpace(settings.GroupName) ? "TwitchAudioPlayer" : settings.GroupName;
    }

    [RelayCommand]
    private void ResetHost() => Host = DefaultHost;

    [RelayCommand]
    private void ResetPort() => Port = DefaultPort;

    [RelayCommand]
    private async Task CreateAsync() => await CreateOrUpdateAsync("Creating OBS scene...");

    [RelayCommand]
    private async Task UpdateAsync() => await CreateOrUpdateAsync("Updating OBS scene...");

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!ValidateConnectionFields())
            return;

        await SaveObsSettingsAsync();
        await RunOperationAsync(async () =>
        {
            var result = await obsSceneSetupService.DeleteAsync(
                BuildConnection(),
                GroupName.Trim());
            SetResult(result);
        }, "Deleting OBS scene...");
    }

    protected override async Task SaveAsync()
    {
        if (!ValidateConnectionFields())
            return;

        await SaveObsSettingsAsync();
        await base.SaveAsync();
    }

    private async Task CreateOrUpdateAsync(string statusText)
    {
        if (_windowTargetFactory is null || _cropFactory is null)
        {
            StatusText = "OBS setup target is not initialized.";
            return;
        }

        if (!ValidateConnectionFields())
            return;

        await SaveObsSettingsAsync();
        await RunOperationAsync(async () =>
        {
            var request = new ObsSceneSetupRequest(
                BuildConnection(),
                GroupName.Trim(),
                _windowTargetFactory(),
                _cropFactory(),
                ObsYouTubeAudioBridgeService.BrowserSourceUrl);
            var result = await obsSceneSetupService.CreateOrUpdateAsync(request);
            SetResult(result);
        }, statusText);
    }

    private async Task RunOperationAsync(Func<Task> operation, string statusText)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = statusText;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetResult(ObsSceneSetupResult result)
    {
        StatusText = result.Warnings.Count == 0
            ? result.Message
            : $"{result.Message}\nWarnings:\n{string.Join("\n", result.Warnings)}";
    }

    private bool ValidateConnectionFields()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            StatusText = "OBS host is empty.";
            return false;
        }

        if (Port is <= 0 or > 65535)
        {
            StatusText = "OBS port must be between 1 and 65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(GroupName))
        {
            StatusText = "OBS scene name is empty.";
            return false;
        }

        return true;
    }

    private ObsConnectionOptions BuildConnection() =>
        new(Host.Trim(), Port, string.IsNullOrEmpty(Password) ? null : Password);

    private async Task SaveObsSettingsAsync()
    {
        var settings = settingsManager.Settings.ObsWebSocket;
        settings.Host = Host.Trim();
        settings.Port = Port;
        settings.Password = Password;
        settings.GroupName = GroupName.Trim();
        await settingsManager.SaveSettingsAsync();
    }
}
