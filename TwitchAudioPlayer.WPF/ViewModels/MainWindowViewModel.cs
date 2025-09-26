using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.WPF.Services;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class MainWindowViewModel(IWindowService windowService) : ObservableObject
{
    [RelayCommand]
    private async Task OnWindowLoadedAsync()
    {
    }

    [RelayCommand]
    private async Task Settings()
    {
        windowService.OpenHotkeySettingsWindow();
    }

    [ObservableProperty] private string _version =
        $"v.{FileVersionInfo.GetVersionInfo((Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).Location)
            .FileVersion ?? "not defined"}";
}