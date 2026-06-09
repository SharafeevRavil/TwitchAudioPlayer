using Windows.Media.Playback;
using FFMediaToolkit.Decoding;
using MusicX.Shared.Player;
using NLog;
using TwitchAudioPlayer.WPF.Services.MusicOrder;
using TwitchAudioPlayer.WPF.Services.Proxy;

namespace TwitchAudioPlayer.WPF.MusicX.Services.Player.Sources;

public class VkMediaSource : MediaSourceBase
{
    private readonly IProxyService _proxyService;
    private readonly Logger _logger;

    public VkMediaSource(IProxyService proxyService, Logger logger)
    {
        _proxyService = proxyService;
        _logger = logger;
    }

    public override async Task<bool> OpenWithMediaPlayerAsync(MediaPlayer player, PlaylistTrack track,
        CancellationToken cancellationToken = default)
    {
        if (track.Data is not VkTrackData { Url.Length: > 0 } vkData) return false;

        var httpProxyUri = vkData is YtTrackData
            ? await _proxyService.EnsureProxyAsync(cancellationToken)
            : null;

        try
        {
            var rtMediaSource = await CreateWinRtMediaSource(vkData, httpProxyUri: httpProxyUri,
                cancellationToken: cancellationToken);

            await rtMediaSource.OpenWithMediaPlayerAsync(player).AsTask(cancellationToken);

            RegisterSourceObjectReference(player, rtMediaSource);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to use winrt decoder for vk media source");

            // i think its better to use task.run over task.yield because we aren't doing async with ffmpeg
            var playbackItem = await Task.Run(() =>
            {
                var file = MediaFile.Open(vkData.Url, CreateMediaOptions(httpProxyUri));

                return CreateMediaPlaybackItem(file);
            }, cancellationToken);

            player.Source = playbackItem;
        }

        return true;
    }
}
