using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace TwitchAudioPlayer.Clients.Clients;

public class YouTubeDownloader
{
    private readonly YoutubeClient _youtube = new();

    public async Task DownloadTrack(string url, string path, CancellationToken cancellationToken = default)
    {
        var video = await GetTrackInfo(url, cancellationToken);
        await DownloadTrack(video, path, cancellationToken);
    }

    private async Task DownloadTrack(Video video, string path, CancellationToken cancellationToken)
    {
        var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id, cancellationToken);
        var audioStreamInfo = streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();

        // ensure directory exists
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directoryPath) && !Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        await using var stream = await _youtube.Videos.Streams.GetAsync(audioStreamInfo, cancellationToken);
        await using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(fileStream, cancellationToken);
    }

    public async Task<Video> GetTrackInfo(string url, CancellationToken cancellationToken = default)
    {
        return await _youtube.Videos.GetAsync(url, cancellationToken);
    }

    public async Task<IStreamInfo> GetTrackStream(string url, CancellationToken cancellationToken = default)
    {
        var video = await GetTrackInfo(url, cancellationToken);
        return await GetTrackStream(video, cancellationToken);
    }

    private async Task<IStreamInfo> GetTrackStream(Video video, CancellationToken cancellationToken)
    {
        var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id, cancellationToken);
        return streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
    }
}