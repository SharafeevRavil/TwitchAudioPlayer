using System.ComponentModel;
using System.Windows.Input;
using TwitchAudioPlayer.Clients.Clients;
using TwitchAudioPlayerWPF.Utils;
using TwitchAudioPlayerWPF.Utils.Commands;

namespace TwitchAudioPlayerWPF.ViewModels;

public class MainViewModel
{
    private readonly VkClient _vkClient;

    public MainViewModel(VkClient vkClient)
    {
        _vkClient = vkClient;
        WindowLoadedCommand = new AsyncRelayCommand(OnWindowLoaded);
    }

    public ICommand WindowLoadedCommand { get; }

    private async Task OnWindowLoaded()
    {

        var list = await _vkClient.GetAudioListAsync("https://vk.com/audios869197500");
    }
}