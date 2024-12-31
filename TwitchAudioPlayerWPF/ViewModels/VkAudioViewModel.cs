using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.Domain.Services;

namespace TwitchAudioPlayerWPF.ViewModels;

public partial class VkAudioViewModel : ObservableObject
{
    private readonly AudioService _audioService;

    public VkAudioViewModel(AudioService audioService)
    {
        _audioService = audioService;
        AudioTracks = [];
    }

    [ObservableProperty] private ObservableCollection<AudioTrackViewModel> _audioTracks;

    [RelayCommand]
    private async Task LoadAudioTracksAsync()
    {
        var tracks = await _audioService.GetAudioTracksAsync("https://vk.com/audios869197500");
        AudioTracks.Clear();
        foreach (var track in tracks)
            AudioTracks.Add(new AudioTrackViewModel(track));
    }
}