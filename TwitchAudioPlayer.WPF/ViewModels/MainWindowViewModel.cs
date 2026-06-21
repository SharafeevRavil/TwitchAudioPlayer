using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IWindowService _windowService;
    private readonly IUserSettingsManager _settingsManager;

    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private string _pinIcon = "PinOff";

    public MainWindowViewModel(IWindowService windowService, IUserSettingsManager settingsManager,
        ApplicationStatusService statusHistory)
    {
        _windowService = windowService;
        _settingsManager = settingsManager;
        StatusHistory = statusHistory;
        IsPinned = settingsManager.Settings.MainWindowTopmost;
        UpdatePinIcon();
    }

    public ApplicationStatusService StatusHistory { get; }

    [RelayCommand]
    private async Task OnWindowLoadedAsync()
    {
    }

    [RelayCommand]
    private async Task Settings()
    {
        _windowService.OpenHotkeySettingsWindow();
    }

    [RelayCommand]
    private void ChatGptSettings()
    {
        _windowService.OpenChatGptSettingsWindow();
    }

    [RelayCommand]
    private void TogglePin()
    {
        IsPinned = !IsPinned;
    }

    partial void OnIsPinnedChanged(bool value)
    {
        UpdatePinIcon();
        _settingsManager.Settings.MainWindowTopmost = value;
        _ = _settingsManager.SaveSettingsSilentlyAsync();
    }

    private void UpdatePinIcon()
    {
        PinIcon = IsPinned ? "Pin" : "PinOff";
    }

    [ObservableProperty] private string _version =
        $"v.{FileVersionInfo.GetVersionInfo((Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).Location)
            .FileVersion ?? "not defined"}";
}
