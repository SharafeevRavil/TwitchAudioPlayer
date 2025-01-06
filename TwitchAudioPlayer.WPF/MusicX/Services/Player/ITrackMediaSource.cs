using Windows.Media.Playback;
using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player;

public interface ITrackMediaSource
{
    Task<bool> OpenWithMediaPlayerAsync(MediaPlayer player, PlaylistTrack track,
        CancellationToken cancellationToken = default);
}