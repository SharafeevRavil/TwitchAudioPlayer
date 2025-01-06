using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class AudioTrackViewModel(PlaylistTrack? audioTrack, bool fillImg = false) : ObservableObject
{
    private bool _isPlaying;

    [ObservableProperty] private bool _fillImg = fillImg;
    [ObservableProperty] private string? _playPauseIcon = "Play";
    public PlaylistTrack? AudioTrack { get; } = audioTrack;

    public string Author => AudioTrack?.GetArtistsString() ?? "Not Found Track";

    public string Title => AudioTrack?.Title ?? "Not Found Track";

    // public string StreamUrl => AudioTrack.StreamUrl;
    public string? ImageLink => AudioTrack?.AlbumId?.CoverUrl ?? "Not Found Track";
    public TimeSpan Duration => AudioTrack != null ? AudioTrack.Data.Duration : TimeSpan.Zero;

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            SetProperty(ref _isPlaying, value);
            PlayPauseIcon = _isPlaying ? "Pause" : "Play";
        }
    }

    public int Index { get; set; }

    public event EventHandler<(AudioTrackViewModel ViewModel, bool IsPlaying)>? PlayPauseRequested;

    [RelayCommand]
    private void PlayPause()
    {
        if(AudioTrack == null) return;
        PlayPauseRequested?.Invoke(this, (this, IsPlaying));
    }
}