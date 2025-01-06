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
}