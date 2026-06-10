using System.Net;
using System.Net.Http;
using MusicX.Shared.Player;
using Serilog;
using TwitchAudioPlayer.WPF.Services.Proxy;
using YoutubeExplode;
using YoutubeExplode.Exceptions;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

namespace TwitchAudioPlayer.WPF.Services.MusicOrder;

public enum YtTrackError
{
    YtNotFound = 10,
    TrackZeroDuration = 20,
    FailedToGetStream = 30,
    FailedToGetInfo = 40,
}

public record YtTrackData(
    string Url,
    bool IsLiked,
    bool IsExplicit,
    bool? HasLyrics,
    TimeSpan Duration,
    IdInfo Info,
    string TrackCode,
    string? ParentBlockId,
    IdInfo? Playlist,
    string? MainColor) : VkTrackData(Url, IsLiked, IsExplicit, HasLyrics, Duration, Info, TrackCode, ParentBlockId,
    Playlist, MainColor);

public class YouTubeService : IDisposable
{
    private static readonly SemaphoreSlim Semaphore = new(5);
    private readonly ILogger _logger = Log.ForContext<YouTubeService>();
    private readonly IProxyService _proxyService;
    private readonly Random _random = new();
    private HttpClient? _httpClient;
    private YoutubeClient? _youtube;
    private Uri? _youtubeProxyUri;

    public YouTubeService(IProxyService proxyService)
    {
        _proxyService = proxyService;
    }

    public async Task<(PlaylistTrack? Track, YtTrackError? Error)> GetPlaylistTrack(string url,
        CancellationToken cancellationToken = default)
    {
        _logger.Information("Start processing YouTube track");
        var (video, infoError) = await GetTrackInfo(url, cancellationToken);
        if (video == null)
        {
            _logger.Warning("Failed to get YouTube video info");
            return (null, infoError ?? YtTrackError.FailedToGetInfo);
        }

        _logger.Information("Received YouTube video info: {Title}", video.Title);
        if (video.Duration == TimeSpan.Zero)
        {
            _logger.Warning("YouTube video duration is zero: {Title}", video.Title);
            return (null, YtTrackError.TrackZeroDuration);
        }

        _logger.Information("Getting YouTube audio stream: {Title}", video.Title);
        var stream = await GetTrackStreamWithRetryAsync(video, cancellationToken);
        if (stream == null)
        {
            _logger.Error("Failed to get YouTube audio stream: {Title}", video.Title);
            return (null, YtTrackError.FailedToGetStream);
        }

        var fakeVkAlbum =
            new VkAlbumId(-1, -1, "", "", video.Thumbnails.Count > 0 ? video.Thumbnails[0].Url : "", null);
        var artists = new List<TrackArtist> { new(video.Author.ChannelTitle, new ArtistId("", ArtistIdType.None)) };
        var fakeVkData = new YtTrackData(stream.Url, false, false, null, video.Duration ?? TimeSpan.MaxValue,
            GetFakeId(), "", null, null, null);
        var playlistTrack = new PlaylistTrack(video.Title, video.Description, fakeVkAlbum, artists, null, fakeVkData);
        _logger.Information("PlaylistTrack created for YouTube video: {Title}", video.Title);
        return (playlistTrack, null);
    }

    public async Task<IStreamInfo?> GetTrackStreamWithRetryAsync(Video video, CancellationToken cancellationToken)
    {
        const int maxRetries = 3;
        const int delay = 2000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            var stream = await GetTrackStream(video, cancellationToken);
            if (stream != null)
                return stream;

            if (attempt < maxRetries)
                await Task.Delay(delay, cancellationToken);
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    private IdInfo GetFakeId() => new(NextLong(_random), NextLong(_random), Guid.NewGuid().ToString());

    private static long NextLong(Random random)
    {
        var buffer = new byte[8];
        random.NextBytes(buffer);
        return BitConverter.ToInt64(buffer, 0);
    }

    private async Task<(Video? Video, YtTrackError? Error)> GetTrackInfo(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var youtube = await CreateYoutubeClientAsync(cancellationToken);
            return (await youtube.Videos.GetAsync(url, cancellationToken), null);
        }
        catch (VideoUnavailableException e)
        {
            _logger.Error(e, "YouTube video is unavailable");
            return (null, YtTrackError.YtNotFound);
        }
        catch (VideoRequiresPurchaseException e)
        {
            _logger.Error(e, "YouTube video requires purchase");
            return (null, YtTrackError.YtNotFound);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while getting YouTube track info");
            return (null, YtTrackError.FailedToGetInfo);
        }
    }

    private async Task<IStreamInfo?> GetTrackStream(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var (video, _) = await GetTrackInfo(url, cancellationToken);
            if (video == null)
                return null;

            return await GetTrackStream(video, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while getting YouTube track stream");
            return null;
        }
    }

    private async Task<IStreamInfo?> GetTrackStream(Video video, CancellationToken cancellationToken)
    {
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            var youtube = await CreateYoutubeClientAsync(cancellationToken);
            var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id, cancellationToken);
            return streamManifest.GetAudioOnlyStreams().GetWithHighestBitrate();
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error while getting stream manifest for video ID: {VideoId}", video.Id);
            return null;
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private async Task<YoutubeClient> CreateYoutubeClientAsync(CancellationToken cancellationToken)
    {
        var proxyUri = await _proxyService.EnsureProxyAsync(cancellationToken);
        if (_youtube is not null && Equals(_youtubeProxyUri, proxyUri))
            return _youtube;

        _httpClient?.Dispose();
        _youtubeProxyUri = proxyUri;

        if (proxyUri is null)
        {
            _httpClient = null;
            _youtube = new YoutubeClient();
            return _youtube;
        }

        var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(proxyUri)
        };

        _httpClient = new HttpClient(handler, disposeHandler: true);
        _youtube = new YoutubeClient(_httpClient);
        return _youtube;
    }
}
