using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.Clients.Clients;

namespace TwitchAudioPlayerWPF.ViewModels;

public partial class MainViewModel(VkClient vkClient) : ObservableObject
{
    private Window? _window;

    public void SetWindow(Window window)
    {
        _window = window;
        _window.StateChanged += OnWindowStateChanged;
    }

    [RelayCommand]
    private async Task OnWindowLoadedAsync()
    {
        // var list = await vkClient.GetAudioListAsync("https://vk.com/audios869197500");
    }

    [RelayCommand]
    private void Minimize()
    {
        if (_window == null) return;
        _window.WindowState = WindowState.Minimized;
    }

    [ObservableProperty] private string? _maximizeIcon = "WindowMaximize";

    [RelayCommand]
    private void Maximize()
    {
        if (_window == null) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) =>
        MaximizeIcon = _window is { WindowState: WindowState.Maximized }
            ? "WindowRestore"
            : "WindowMaximize";

    [RelayCommand]
    private static void Close() => Application.Current.Shutdown();
}