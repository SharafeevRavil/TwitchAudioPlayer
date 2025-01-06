using TwitchAudioPlayer.WPF.MusicX.Services.Player.Playlists;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player;

public record PlayerState(IPlaylist Playlist, int CurrentIndex, TimeSpan Position)
{
    public static PlayerState? CreateOrNull(PlayerService service)
    {
        return service is { CurrentPlaylist: null } or { CurrentTrack: null }
            ? null
            : new PlayerState(service.CurrentPlaylist, service.CurrentIndex, service.Position);
    }
}