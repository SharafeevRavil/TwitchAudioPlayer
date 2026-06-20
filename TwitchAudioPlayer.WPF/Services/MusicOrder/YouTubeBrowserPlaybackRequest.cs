using MusicX.Shared.Player;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public enum BrowserPlaybackOwner
{
    None,
    MusicOrder,
    VkReplacement
}

public sealed record YouTubeBrowserPlaybackRequest(
    string VideoId,
    TimeSpan StartPosition,
    PlaylistTrack Track,
    int RequestId,
    BrowserPlaybackOwner Owner = BrowserPlaybackOwner.MusicOrder);
