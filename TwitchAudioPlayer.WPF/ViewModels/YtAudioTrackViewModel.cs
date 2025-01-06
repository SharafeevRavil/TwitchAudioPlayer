using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicX.Shared.Player;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

namespace TwitchAudioPlayer.WPF.ViewModels;

public partial class YtAudioTrackViewModel : ObservableObject
{
    public MusicOrderWithTrack AudioTrack { get; }

    [ObservableProperty] private string _sourceImage;
    [ObservableProperty] private AudioTrackViewModel _audioTrackViewModel;

    /// <inheritdoc/>
    public YtAudioTrackViewModel(MusicOrderWithTrack audioTrack)
    {
        AudioTrack = audioTrack;
        AudioTrackViewModel = new AudioTrackViewModel(AudioTrack.PlaylistTrack, true);

        SourceImage = audioTrack.MusicOrder.Type switch
        {
            OrderType.Twitch => "pack://application:,,,/Assets/icons/twitch.png",
            OrderType.DonationAlerts => "pack://application:,,,/Assets/icons/da.png",
            _ => ""
        };
    }

    // public event EventHandler<(AudioTrackViewModel ViewModel, PlaylistTrack Track, bool IsPlaying)>? PlayPauseRequested;
    //
    // [RelayCommand]
    // private void PlayPause()
    // {
    //     PlayPauseRequested?.Invoke(this, (this, AudioTrack, IsPlaying));
    // }
}