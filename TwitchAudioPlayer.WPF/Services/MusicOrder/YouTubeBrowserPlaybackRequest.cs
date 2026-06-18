using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public sealed record YouTubeBrowserPlaybackRequest(
    string VideoId,
    TimeSpan StartPosition,
    PlaylistTrack Track);
