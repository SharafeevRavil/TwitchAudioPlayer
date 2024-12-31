using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.Clients.Clients;

namespace TwitchAudioPlayerWPF.ViewModels;

public partial class MainWindowViewModel(VkClient vkClient) : ObservableObject
{
    [RelayCommand]
    private async Task OnWindowLoadedAsync()
    {
        // var list = await vkClient.GetAudioListAsync("https://vk.com/audios869197500");
    }
}