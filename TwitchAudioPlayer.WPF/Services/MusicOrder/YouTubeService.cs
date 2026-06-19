using MusicX.Shared.Player;
using Serilog;
using TwitchAudioPlayer.WPF.Services;
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

public class YouTubeService
{
    private static readonly SemaphoreSlim Semaphore = new(5);
    private readonly ILogger _logger = Log.ForContext<YouTubeService>();
    private readonly IUserSettingsManager _settingsManager;
    private readonly Random _random = new();
    private readonly YoutubeClient _youtube = new();

    public YouTubeService(IUserSettingsManager settingsManager)
    {
        _settingsManager = settingsManager;
    }

    public async Task<(PlaylistTrack? Track, YtTrackError? Error)> GetPlaylistTrack(string url,
        CancellationToken cancellationToken = default)
    {
        var useBrowserPlayback = _settingsManager.Settings.YouTubePlaybackMode == YouTubePlaybackMode.Browser;

        _logger.Information("Start processing YouTube track");
        var (video, infoError) = await GetTrackInfo(url, cancellationToken);
        if (video == null)
        {
            if (useBrowserPlayback && TryExtractYouTubeId(url) is { Length: > 0 } videoId)
            {
                _logger.Warning("Failed to get YouTube video info, creating browser-only track for video ID: {VideoId}",
                    videoId);
                return (CreateBrowserOnlyTrack(url, videoId), null);
            }

            _logger.Warning("Failed to get YouTube video info");
            return (null, infoError ?? YtTrackError.FailedToGetInfo);
        }

        _logger.Information("Received YouTube video info: {Title}", video.Title);
        if (video.Duration == TimeSpan.Zero)
        {
            _logger.Warning("YouTube video duration is zero: {Title}", video.Title);
            return (null, YtTrackError.TrackZeroDuration);
        }

        IStreamInfo? stream = null;
        if (!useBrowserPlayback)
        {
            _logger.Information("Getting YouTube audio stream: {Title}", video.Title);
            stream = await GetTrackStreamWithRetryAsync(video, cancellationToken);
            if (stream == null)
            {
                _logger.Error("Failed to get YouTube audio stream: {Title}", video.Title);
                return (null, YtTrackError.FailedToGetStream);
            }
        }

        var fakeVkAlbum =
            new VkAlbumId(-1, -1, "", "", video.Thumbnails.Count > 0 ? video.Thumbnails[0].Url : "", null);
        var artists = new List<TrackArtist> { new(video.Author.ChannelTitle, new ArtistId("", ArtistIdType.None)) };
        var fakeVkData = new YtTrackData(stream?.Url ?? url, false, false, null, video.Duration ?? TimeSpan.MaxValue,
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

    private IdInfo GetFakeId() => new(NextLong(_random), NextLong(_random), Guid.NewGuid().ToString());

    private static long NextLong(Random random)
    {
        var buffer = new byte[8];
        random.NextBytes(buffer);
        return BitConverter.ToInt64(buffer, 0);
    }

    private PlaylistTrack CreateBrowserOnlyTrack(string url, string videoId)
    {
        var fakeVkAlbum = new VkAlbumId(-1, -1, "", "", "", null);
        var artists = new List<TrackArtist> { new("YouTube", new ArtistId("", ArtistIdType.None)) };
        var fakeVkData = new YtTrackData(url, false, false, null,
            TimeSpan.FromMinutes(Math.Max(1, _settingsManager.Settings.MaxMinutesLength)),
            GetFakeId(), "", null, null, null);

        return new PlaylistTrack($"YouTube: {videoId}", url, fakeVkAlbum, artists, null, fakeVkData);
    }

    private static string? TryExtractYouTubeId(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return null;

        if (parsed.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
            return parsed.AbsolutePath.Trim('/').Split('/').FirstOrDefault();

        if (parsed.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = parsed.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(pair => pair.Length == 2 && pair[0] == "v");
            if (query != null)
                return Uri.UnescapeDataString(query[1]);

            var segments = parsed.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed")
                return segments[1];
        }

        return null;
    }

    private async Task<(Video? Video, YtTrackError? Error)> GetTrackInfo(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _youtube.Videos.GetAsync(url, cancellationToken), null);
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

    private async Task<IStreamInfo?> GetTrackStream(Video video, CancellationToken cancellationToken)
    {
        await Semaphore.WaitAsync(cancellationToken);
        try
        {
            var streamManifest = await _youtube.Videos.Streams.GetManifestAsync(video.Id, cancellationToken);
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
}
