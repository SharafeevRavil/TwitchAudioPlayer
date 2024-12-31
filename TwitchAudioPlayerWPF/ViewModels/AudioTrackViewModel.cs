using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TwitchAudioPlayer.Domain.Models;

namespace TwitchAudioPlayerWPF.ViewModels;

public partial class AudioTrackViewModel(AudioTrack audioTrack) : ObservableObject
{
    public AudioTrack AudioTrack { get; } = audioTrack;

    public string Author => AudioTrack.Artist;
    public string Title => AudioTrack.Title;
    public string? DownloadLink => AudioTrack.DownloadLink;
    public string? ImageLink => AudioTrack.ImageLink;
    public TimeSpan Duration => AudioTrack.Duration;
    
    
    public bool IsPlaying { get; set; }
    
    [ObservableProperty]
    private string? _playPauseIcon = "Play";

    private void UpdatePlayPauseIcon() => PlayPauseIcon = IsPlaying ? "Pause" : "Play";

    [RelayCommand]
    public async Task PlayPauseAsync()
    {
        IsPlaying = !IsPlaying;
        UpdatePlayPauseIcon();
        // Логика воспроизведения/паузы
    }
}